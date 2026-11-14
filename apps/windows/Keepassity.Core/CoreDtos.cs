// SPDX-License-Identifier: GPL-3.0-or-later
// Wire types for the keepassity FFI v1 JSON contract. They live in Core so the
// application-service layer (sync orchestration, backups) can depend on them
// without referencing the WinUI shell.
using System.Text.Json.Serialization;

namespace Keepassity.Core;

public sealed class GroupDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("parent_uuid")] public string? ParentUuid { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("icon")] public int Icon { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
}

public sealed class EntrySummaryDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("group_uuid")] public string GroupUuid { get; set; } = "";
    [JsonPropertyName("has_totp")] public bool HasTotp { get; set; }
    [JsonPropertyName("modified_unix")] public long ModifiedUnix { get; set; }
}

public sealed class EntryFullDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("group_uuid")] public string GroupUuid { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("password_secret")] public string PasswordSecret { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("custom_fields")] public List<CustomFieldDto> CustomFields { get; set; } = new();
    [JsonPropertyName("times")] public TimesDto Times { get; set; } = new();
    [JsonPropertyName("history_count")] public int HistoryCount { get; set; }
    [JsonPropertyName("totp")] public TotpConfigDto? Totp { get; set; }
}

public sealed class CustomFieldDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
    [JsonPropertyName("protected")] public bool Protected { get; set; }
}

public sealed class TimesDto
{
    [JsonPropertyName("created_unix")] public long? CreatedUnix { get; set; }
    [JsonPropertyName("modified_unix")] public long? ModifiedUnix { get; set; }
}

public sealed class TotpConfigDto
{
    [JsonPropertyName("secret_base32")] public string SecretBase32 { get; set; } = "";
    [JsonPropertyName("issuer")] public string? Issuer { get; set; }
    [JsonPropertyName("account")] public string? Account { get; set; }
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "SHA1";
    [JsonPropertyName("digits")] public int Digits { get; set; } = 6;
    [JsonPropertyName("period")] public int Period { get; set; } = 30;
    [JsonPropertyName("encoder")] public string Encoder { get; set; } = "totp";
}

public sealed class TotpCodeDto
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("remaining")] public int Remaining { get; set; }
    [JsonPropertyName("period")] public int Period { get; set; } = 30;
    [JsonPropertyName("digits")] public int Digits { get; set; } = 6;
    [JsonPropertyName("algorithm")] public string Algorithm { get; set; } = "SHA1";
    [JsonPropertyName("issuer")] public string? Issuer { get; set; }
    [JsonPropertyName("account")] public string? Account { get; set; }
}

public sealed class PasswordGenerateDto
{
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    [JsonPropertyName("entropy_bits")] public double EntropyBits { get; set; }
}

public sealed class SearchResponseDto
{
    [JsonPropertyName("matches")] public List<SearchMatchDto> Matches { get; set; } = new();
}

public sealed class SearchMatchDto
{
    [JsonPropertyName("entry")] public EntrySummaryDto Entry { get; set; } = new();
    [JsonPropertyName("matched_fields")] public List<string> MatchedFields { get; set; } = new();
}

public sealed class CreateEntryResponse
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
}

public sealed class CreateGroupResponse
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
}

public sealed class ErrorDto
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class SyncDecisionDto
{
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("degraded")] public bool Degraded { get; set; }
    [JsonPropertyName("retry_after_ms")] public long? RetryAfterMs { get; set; }
}

public sealed class SyncConflictDto
{
    [JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("local_modified_unix")] public long? LocalModifiedUnix { get; set; }
    [JsonPropertyName("remote_modified_unix")] public long? RemoteModifiedUnix { get; set; }
    [JsonPropertyName("fields")] public List<string> Fields { get; set; } = new();
}

public sealed record SyncMergeResult(byte[] Bytes, List<SyncConflictDto> Conflicts);
