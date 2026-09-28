namespace Wlrix.Archiver.ViewModels;

/// <summary>What the user chose when offered encryption for a new archive.</summary>
/// <param name="Password">
/// The password, or empty for an unencrypted archive. Empty is a real answer here and is not
/// the same as declining, which is a null result from the prompt: one creates the archive in
/// the clear, the other creates nothing.
/// </param>
/// <param name="EncryptNames">
/// Whether the entry names are encrypted too. Off by default, and deliberately: it is what the
/// tools these archives will be opened with default to, and it changes the archive noticeably —
/// one whose names are encrypted cannot be listed at all without the password, where one whose
/// names are not can be browsed and only its contents withheld.
/// </param>
public sealed record NewArchiveEncryption(string Password, bool EncryptNames);
