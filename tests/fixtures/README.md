# 测试语料（KDBX 夹具）

> **没有任何真实密码或真实库**。密码都是公开的测试密码，仅供本地/CI 使用。
> 真实密码库**永远不入库**（`.gitignore` 已排除，仅允许 `tests/fixtures/**/*.kdbx`）。

## 来源

| 来源 | 数量 | 许可证 | 说明 |
|---|---|---|---|
| [keepass-rs](https://github.com/sseemayer/keepass-rs) `tests/resources/` | 25 | MIT | 上游测试样本，覆盖 KDBX3.1/4.0/4.1、AES-KDF/Argon2/Argon2id、AES/ChaCha20/Twofish、附件、保护字段、TOTP、删除记录、损坏文件 |
| 本仓库自建 | 0 | GPL-3.0-or-later | 需要「500 条目 / 非 ASCII 路径 / 真实 KeePass 2.x 形态」时用 KeePassLib 工具生成 |

## 主密码（测试用）

| 密码 | 适用文件 |
|---|---|
| `demopass` | 除下面列出的以外全部 |
| `test` | `test_db_kdbx4_with_totp_entry.kdbx`、`test_db_kdbx4_with_totp_sha512_entry.kdbx` |
| `password` | `test_db_kdbx3_with_chacha20_protected_fields.kdbx` |
| （需密钥文件） | `test_db*_with_keyfile*.kdbx`、`test_db_with_challenge_response_key.kdbx` —— v1.0 只支持主密码，这些暂不作验收依据 |

## 只读验证结果（2026-10-25）

工具：`keepassity-cli inspect <file> --password <pw>`（读取用 `keepass` crate 0.17.0）

| 文件 | 结果 | 备注 / 条目数 |
|---|---|---|
| broken_kdbx_version.kdbx | EXPECT-FAIL | cannot read database: VersionParse(InvalidKDBXVersion { version: 3041655655, file_major_version: 42, file_minor_version: 0 }) |
| broken_random_data.kdbx | EXPECT-FAIL | cannot read database: VersionParse(InvalidKDBXIdentifier) |
| test_db_few_rounds.kdbx | OK | entries=1 totp=0 |
| test_db_kdb3_with_file_larger_1mb.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx3_with_attachments.kdbx | OK | entries=2 totp=0 |
| test_db_kdbx3_with_chacha20_protected_fields.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx4_with_keyfile.kdbx | FAIL | cannot read database: Key(IncorrectKey) |
| test_db_kdbx4_with_keyfile_v2.kdbx | FAIL | cannot read database: Key(IncorrectKey) |
| test_db_kdbx4_with_keyfile_v2_alt.kdbx | FAIL | cannot read database: Key(IncorrectKey) |
| test_db_kdbx4_with_password_aes.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx4_with_password_argon2.kdbx | OK | entries=2 totp=0 |
| test_db_kdbx4_with_password_argon2_chacha20.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx4_with_password_argon2_twofish.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx4_with_password_argon2id.kdbx | OK | entries=2 totp=0 |
| test_db_kdbx4_with_password_argon2id_chacha20.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx4_with_password_argon2id_twofish.kdbx | OK | entries=1 totp=0 |
| test_db_kdbx4_with_password_deleted_entry.kdbx | OK | entries=3 totp=0 |
| test_db_kdbx4_with_totp_entry.kdbx | OK | entries=1 totp=1 |
| test_db_kdbx4_with_totp_sha512_entry.kdbx | OK | entries=1 totp=1 |
| test_db_kdbx41_features.kdbx | OK | entries=2 totp=0 |
| test_db_kdbx41_with_password_aes.kdbx | OK | entries=4 totp=0 |
| test_db_with_challenge_response_key.kdbx | FAIL | cannot read database: Key(IncorrectKey) |
| test_db_with_keyfile.kdbx | FAIL | cannot read database: Cryptography(InvalidPadding(Error)) |
| test_db_with_keyfile_xml.kdbx | FAIL | cannot read database: Key(IncorrectKey) |
| test_db_with_password.kdbx | OK | entries=6 totp=0 |

**结论**：KDBX 3.1 / 4.0 / 4.1 主密码库全部可读；KDF（AES-KDF / Argon2d / Argon2id）与加密算法（AES / ChaCha20 / Twofish）覆盖；附件、保护字段、TOTP 字段、删除记录均可识别。需密钥文件的样本按 v1.0 范围排除。

## 观测到的错误分类（用于 §5.6「密码错 vs 文件损坏」）

| 情况 | crate 返回 | 我们要翻译成 |
|---|---|---|
| KDBX4 主密码错误 | `Key(IncorrectKey)` | `BadPassword` |
| KDBX3 主密码错误 | `Cryptography(InvalidPadding)` | `BadPassword`（结合头部合法性判断） |
| 文件头损坏 | `VersionParse(...)` / `InvalidKDBXIdentifier` | `CorruptDatabase` |
| 需密钥文件但只给密码 | `Key(IncorrectKey)` | `BadPassword`（提示「可能还需要密钥文件」） |

## 使用规则

1. 夹具只放合成/公开样本；真实库副本仅本地使用，**不入库**。
2. 新增夹具必须在本表登记：文件名、来源、许可证、密码、用途。
3. 写入路径改动必须至少跑：`kdbx41_features`、`kdbx4_with_password_argon2id_chacha20`、`kdbx3_with_attachments`、`deleted_entry`、两个 TOTP 样本。
