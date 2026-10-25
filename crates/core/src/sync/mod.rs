// SPDX-License-Identifier: GPL-3.0-or-later
//! Sync decision state machine (pure; no HTTP/file IO).
//!
//! Implements the plan §5.3 edges: remote unchanged/clean → UpToDate; unchanged/
//! dirty → Upload; changed/clean → Download; both changed → DownloadAndMerge;
//! network failure → Offline; PUT 412 → three exponential back-offs then
//! Conflict. Servers without ETag fall back to SHA-256/size/mtime and set
//! `degraded`.

use crate::merge::{merge, MergeConflict, MergeReport};
use crate::model::Database;
use sha2::{Digest, Sha256};

pub mod conflict;

/// What the caller should do next.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SyncActionKind {
    /// Nothing to transfer.
    UpToDate,
    /// Upload local bytes (If-Match baseline ETag).
    Upload,
    /// Download remote bytes and adopt them.
    Download,
    /// Download, merge remote into local, then upload the merge result.
    DownloadAndMerge,
    /// Exponential back-off before retrying PUT.
    Retry,
    /// Network/auth failure; keep dirty state and retry later.
    Offline,
    /// Three 412 retries exhausted (or merge conflict needs the UI).
    Conflict,
}

/// Decision returned to the platform layer.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SyncDecision {
    /// Next action.
    pub action: SyncActionKind,
    /// Human-readable reason (no secrets).
    pub reason: String,
    /// Server lacks ETag; reliability degraded.
    pub degraded: bool,
    /// Retry delay for `Retry` decisions (milliseconds).
    pub retry_after_ms: Option<u64>,
}

/// Persisted sync baseline (sidecar); never stored inside the KDBX.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct SyncBaseline {
    /// ETag from the last successful sync.
    pub etag: Option<String>,
    /// SHA-256 of the last successfully synced bytes.
    pub sha256: Option<String>,
    /// Size of the last successfully synced bytes.
    pub size: Option<u64>,
    /// Remote last-modified time of the last successful sync.
    pub modified_unix: Option<i64>,
}

/// Remote metadata obtained by PROPFIND (no download).
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct RemoteMeta {
    /// ETag if the server provides one.
    pub etag: Option<String>,
    /// Content length.
    pub size: u64,
    /// Last modified time.
    pub modified_unix: Option<i64>,
    /// Optional server-provided content hash.
    pub sha256: Option<String>,
}

impl RemoteMeta {
    /// ETag persistence uses strong/weak tags verbatim.
    pub fn is_degraded(&self) -> bool {
        self.etag.is_none()
    }
}

/// Sync state kept by the caller/sidecar.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SyncState {
    /// Last successful baseline.
    pub baseline: SyncBaseline,
    /// Local has unsaved-to-remote changes.
    pub local_dirty: bool,
    upload_conflicts: u32,
}

impl SyncState {
    /// New clean state with an empty baseline.
    pub fn new(baseline: SyncBaseline) -> Self {
        Self {
            baseline,
            local_dirty: false,
            upload_conflicts: 0,
        }
    }

    /// Mark that the local database changed and should be uploaded.
    pub fn mark_dirty(&mut self) {
        self.local_dirty = true;
    }

    /// Decide the next action from remote metadata.
    pub fn decide(&self, remote: &RemoteMeta) -> SyncDecision {
        let degraded = remote.is_degraded();
        let unchanged = self.remote_matches_baseline(remote);
        let (action, reason) = if unchanged {
            if self.local_dirty {
                (SyncActionKind::Upload, "remote unchanged, local dirty")
            } else {
                (SyncActionKind::UpToDate, "remote unchanged, local clean")
            }
        } else if self.local_dirty {
            (
                SyncActionKind::DownloadAndMerge,
                "remote changed, local dirty: download and merge",
            )
        } else {
            (SyncActionKind::Download, "remote changed, local clean")
        };
        SyncDecision {
            action,
            reason: reason.to_string(),
            degraded,
            retry_after_ms: None,
        }
    }

    /// Compare baseline to current remote metadata, including degraded mode.
    fn remote_matches_baseline(&self, remote: &RemoteMeta) -> bool {
        if let (Some(baseline), Some(remote_etag)) =
            (self.baseline.etag.as_deref(), remote.etag.as_deref())
        {
            return baseline == remote_etag;
        }
        if let (Some(baseline), Some(remote_sha)) =
            (self.baseline.sha256.as_deref(), remote.sha256.as_deref())
        {
            return baseline == remote_sha;
        }
        match (
            self.baseline.size,
            self.baseline.modified_unix,
            remote.modified_unix,
        ) {
            (Some(size), Some(modified), Some(remote_modified)) => {
                size == remote.size && modified == remote_modified
            }
            _ => false,
        }
    }

    /// Record a successful upload/download (baseline commit).
    pub fn commit_success(
        &mut self,
        etag: Option<String>,
        sha256: String,
        size: u64,
        modified_unix: Option<i64>,
    ) {
        self.baseline = SyncBaseline {
            etag,
            sha256: Some(sha256),
            size: Some(size),
            modified_unix,
        };
        self.local_dirty = false;
        self.upload_conflicts = 0;
    }

    /// Handle a 412 (remote changed while uploading): retry three times with
    /// 0.5 s / 1.5 s / 4 s back-off, then surface a conflict.
    pub fn on_upload_412(&mut self) -> SyncDecision {
        self.upload_conflicts += 1;
        let delay = match self.upload_conflicts {
            1 => 500,
            2 => 1500,
            3 => 4000,
            _ => 0,
        };
        if self.upload_conflicts <= 3 {
            SyncDecision {
                action: SyncActionKind::Retry,
                reason: format!("PUT 412, back-off retry {}", self.upload_conflicts),
                degraded: false,
                retry_after_ms: Some(delay),
            }
        } else {
            SyncDecision {
                action: SyncActionKind::Conflict,
                reason: "PUT 412 after three back-offs: conflict requires manual resolution"
                    .to_string(),
                degraded: false,
                retry_after_ms: None,
            }
        }
    }

    /// Network/auth failure: stay dirty and report Offline.
    pub fn on_offline(&self, reason: impl Into<String>) -> SyncDecision {
        SyncDecision {
            action: SyncActionKind::Offline,
            reason: reason.into(),
            degraded: false,
            retry_after_ms: None,
        }
    }

    /// Reset the conflict counter after a different failure/success path.
    pub fn reset_upload_conflicts(&mut self) {
        self.upload_conflicts = 0;
    }
}

/// Result of a two-database merge during sync.
#[derive(Clone, Debug)]
pub struct SyncMergeResult {
    /// Merged model, ready to be written and uploaded.
    pub database: Database,
    /// Merge counters.
    pub report: MergeReport,
    /// Per-item conflict details for the three-way UI.
    pub conflicts: Vec<MergeConflict>,
    /// There are same-mtime conflicts that need UI resolution.
    pub has_conflicts: bool,
}

/// Merge local/remote models for the `DownloadAndMerge` path.
pub fn merge_for_sync(local: &Database, remote: &Database) -> SyncMergeResult {
    let (database, report) = merge(local, remote);
    let has_conflicts = report.conflicts > 0;
    let conflicts = report.conflict_items.clone();
    SyncMergeResult {
        database,
        report,
        conflicts,
        has_conflicts,
    }
}

/// SHA-256 of local bytes (hex), used as the local dirty hash.
pub fn local_hash(bytes: &[u8]) -> String {
    let digest = Sha256::digest(bytes);
    digest.iter().map(|byte| format!("{byte:02x}")).collect()
}

/// SHA-256 of a canonical model dump (redacted model); useful for diagnostics.
pub fn model_hash(db: &Database) -> Result<String, String> {
    let json = db.canonical_json().map_err(|error| error.to_string())?;
    Ok(local_hash(json.as_bytes()))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn baseline(etag: &str, sha: &str) -> SyncBaseline {
        SyncBaseline {
            etag: Some(etag.to_string()),
            sha256: Some(sha.to_string()),
            size: Some(100),
            modified_unix: Some(1000),
        }
    }

    fn remote(etag: Option<&str>, sha: Option<&str>) -> RemoteMeta {
        RemoteMeta {
            etag: etag.map(str::to_string),
            size: 100,
            modified_unix: Some(1000),
            sha256: sha.map(str::to_string),
        }
    }

    fn clean_state() -> SyncState {
        SyncState::new(baseline("\"abc\"", "sha-abc"))
    }

    #[test]
    fn remote_unchanged_clean_is_up_to_date() {
        let decision = clean_state().decide(&remote(Some("\"abc\""), Some("sha-abc")));
        assert_eq!(decision.action, SyncActionKind::UpToDate);
        assert!(!decision.degraded);
    }

    #[test]
    fn remote_unchanged_dirty_uploads() {
        let mut state = clean_state();
        state.mark_dirty();
        let decision = state.decide(&remote(Some("\"abc\""), Some("sha-abc")));
        assert_eq!(decision.action, SyncActionKind::Upload);
    }

    #[test]
    fn remote_changed_clean_downloads() {
        let decision = clean_state().decide(&remote(Some("\"changed\""), Some("sha-new")));
        assert_eq!(decision.action, SyncActionKind::Download);
    }

    #[test]
    fn remote_changed_dirty_merges() {
        let mut state = clean_state();
        state.mark_dirty();
        let decision = state.decide(&remote(Some("\"changed\""), Some("sha-new")));
        assert_eq!(decision.action, SyncActionKind::DownloadAndMerge);
    }

    #[test]
    fn missing_etag_degrades_and_matches_by_sha() {
        let mut state = SyncState::new(SyncBaseline {
            etag: None,
            sha256: Some("sha-abc".to_string()),
            size: Some(100),
            modified_unix: Some(1000),
        });
        let decision = state.decide(&remote(None, Some("sha-abc")));
        assert_eq!(decision.action, SyncActionKind::UpToDate);
        assert!(decision.degraded);
        state.mark_dirty();
        let decision = state.decide(&remote(None, Some("sha-abc")));
        assert_eq!(decision.action, SyncActionKind::Upload);
        assert!(decision.degraded);
    }

    #[test]
    fn missing_etag_with_new_sha_needs_merge() {
        let mut state = SyncState::new(SyncBaseline {
            etag: None,
            sha256: Some("sha-abc".to_string()),
            size: Some(100),
            modified_unix: Some(1000),
        });
        state.mark_dirty();
        let decision = state.decide(&RemoteMeta {
            etag: None,
            size: 120,
            modified_unix: Some(2000),
            sha256: Some("sha-new".to_string()),
        });
        assert_eq!(decision.action, SyncActionKind::DownloadAndMerge);
        assert!(decision.degraded);
    }

    #[test]
    fn put_412_retries_three_times_then_conflicts() {
        let mut state = clean_state();
        state.mark_dirty();
        let first = state.on_upload_412();
        assert_eq!(first.action, SyncActionKind::Retry);
        assert_eq!(first.retry_after_ms, Some(500));
        let second = state.on_upload_412();
        assert_eq!(second.retry_after_ms, Some(1500));
        let third = state.on_upload_412();
        assert_eq!(third.retry_after_ms, Some(4000));
        let fourth = state.on_upload_412();
        assert_eq!(fourth.action, SyncActionKind::Conflict);
        assert_eq!(fourth.retry_after_ms, None);
    }

    #[test]
    fn offline_keeps_dirty_state() {
        let mut state = clean_state();
        state.mark_dirty();
        let decision = state.on_offline("network down");
        assert_eq!(decision.action, SyncActionKind::Offline);
        assert!(state.local_dirty);
    }

    #[test]
    fn commit_updates_baseline_and_clears_dirty() {
        let mut state = clean_state();
        state.mark_dirty();
        state.commit_success(
            Some("\"new\"".to_string()),
            "sha-new".to_string(),
            120,
            Some(2000),
        );
        assert!(!state.local_dirty);
        assert_eq!(state.baseline.etag.as_deref(), Some("\"new\""));
        assert_eq!(state.baseline.sha256.as_deref(), Some("sha-new"));
        assert_eq!(state.baseline.size, Some(120));
        assert_eq!(
            state.decide(&remote(Some("\"new\""), None)).action,
            SyncActionKind::UpToDate
        );
    }

    #[test]
    fn local_hash_is_sha256_hex() {
        assert_eq!(
            local_hash(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
        assert_eq!(local_hash(b"abc").len(), 64);
    }

    #[test]
    fn full_sync_cycle_merges_commits_and_returns_clean() {
        let local = Database::default();
        let remote = Database::default();
        let mut state = clean_state();
        state.mark_dirty();
        let decision = state.decide(&remote_meta_with_etag("\"remote\""));
        assert_eq!(decision.action, SyncActionKind::DownloadAndMerge);
        let merged = merge_for_sync(&local, &remote);
        assert!(!merged.has_conflicts);
        state.commit_success(
            Some("\"remote\"".to_string()),
            local_hash(b"merged"),
            10,
            Some(1),
        );
        assert!(!state.local_dirty);
        assert_eq!(
            state.decide(&remote_meta_with_etag("\"remote\"")).action,
            SyncActionKind::UpToDate
        );
    }

    fn remote_meta_with_etag(etag: &str) -> RemoteMeta {
        RemoteMeta {
            etag: Some(etag.to_string()),
            size: 10,
            modified_unix: Some(1),
            sha256: None,
        }
    }
}
