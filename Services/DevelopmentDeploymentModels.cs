namespace HomeAssistantMCPSharp.Services;

public sealed class DevelopmentDeployment
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public string? EntryFile { get; set; }
    public bool RemoveMissingFiles { get; set; }
    public string State { get; set; } = "staging";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public DateTimeOffset? AppliedUtc { get; set; }
    public DateTimeOffset? VerifiedUtc { get; set; }
    public Dictionary<string, DevelopmentUploadFile> Files { get; set; } = new(StringComparer.Ordinal);
    public DevelopmentPreview? Preview { get; set; }
    public Dictionary<string, string> AppliedHashes { get; set; } = new(StringComparer.Ordinal);
    public string? ConfirmationHash { get; set; }
    public DateTimeOffset? ConfirmationExpiresUtc { get; set; }
    public string? ConfirmationAction { get; set; }
    public string? ResourceUrl { get; set; }
    public string? PreviousResourceId { get; set; }
    public string? PreviousResourceUrl { get; set; }
    public string? PreviousResourceType { get; set; }
    public bool ResourceCreated { get; set; }
    public object? ConfigCheck { get; set; }
    public string? LastError { get; set; }
}

public sealed class DevelopmentUploadFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Complete { get; set; }
}

public sealed class DevelopmentPreview
{
    public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public List<DevelopmentChange> Changes { get; set; } = [];
    public Dictionary<string, string> CurrentHashes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ProposedHashes { get; set; } = new(StringComparer.Ordinal);
    public long ProposedBytes { get; set; }
    public bool RestartRequired { get; set; }
}

public sealed record DevelopmentChange(string Path, string Change, string? CurrentSha256, string? ProposedSha256);

public sealed record DevelopmentConfirmation(string Token, DateTimeOffset ExpiresUtc);
