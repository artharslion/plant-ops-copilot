namespace PlantOps.Domain.Knowledge;

public sealed class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PlantId
    {
        get; set;
    }

    public Guid? AssetId
    {
        get; set;
    }

    public string Title { get; set; } = "";

    public string Category { get; set; } = "Maintenance";

    public string Visibility { get; set; } = "PlantReaders";

    public string[] AllowedRoles { get; set; } = [];

    public Guid? CurrentPublishedVersionId
    {
        get; set;
    }

    public bool IsArchived
    {
        get; set;
    }

    public long Version { get; set; } = 1;
}

public sealed class DocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId
    {
        get; set;
    }

    public int VersionNumber
    {
        get; set;
    }

    public string FileName { get; set; } = "";

    public string MediaType { get; set; } = "";

    public string StorageKey { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public long SizeBytes
    {
        get; set;
    }

    public string Status { get; set; } = "Draft";

    public string UploadedByUserId { get; set; } = "";

    public DateTimeOffset UploadedAt
    {
        get; set;
    }

    public DateTimeOffset? PublishedAt
    {
        get; set;
    }

    public string IndexStatus { get; set; } = "NotRequested";

    public string? ParserVersion
    {
        get; set;
    }
}

public sealed class DocumentChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId
    {
        get; set;
    }

    public Guid VersionId
    {
        get; set;
    }

    public int Ordinal
    {
        get; set;
    }

    public string Section { get; set; } = "";

    public int StartLine
    {
        get; set;
    }

    public int EndLine
    {
        get; set;
    }

    public string Text { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string ParserVersion { get; set; } = "";
}
