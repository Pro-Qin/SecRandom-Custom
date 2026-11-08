namespace SecRandom.Shared.Updates;

public enum UpdateChannel
{
    Release,
    Beta,
    Alpha
}

public enum UpdateSource
{
    GitHub,
    GitHubMirror,

    // 只服务仓库内文件的镜像通道（jsDelivr 按仓库路径取 metadata.yaml），不提供 release 资产
    JsDelivr
}

/// <summary>
/// 更新检查的取源策略。custom-only：特供构建允许在直连与镜像之间切换，避免校园网等弱网环境检查失败。
/// </summary>
public enum UpdateSourceMode
{
    /// <summary>先直连，失败或超时后按顺序降级到镜像。</summary>
    Auto,

    /// <summary>只用官方 GitHub 直连。</summary>
    Direct,

    /// <summary>只用镜像通道。</summary>
    Mirror
}

public enum UpdatePackageKind
{
    PortableZip,
    WindowsExe,
    LinuxDeb,
    MacosPkg,
    MacosApp,
    AndroidApk
}

public enum UpdateRuntimeKind
{
    FrameworkDependent,
    SelfContained
}

public enum UpdateOperationPhase
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    Verifying,
    ReadyToInstall,
    Installing,
    Restarting,
    CheckFailed,
    DownloadFailed,
    VerificationFailed,
    InstallFailed,
    Cancelled
}


public sealed class UpdateManifest
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public DateTimeOffset PublishedAt { get; init; }
    public string? NotesUrl { get; init; }
    public List<UpdateArtifact> Artifacts { get; init; } = [];
}

public sealed class UpdateArtifact
{
    public string Id { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Arch { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string RuntimeKind { get; init; } = string.Empty;
    public string AssetName { get; init; } = string.Empty;
    public long ByteLength { get; init; }
    public string Sha512 { get; init; } = string.Empty;
    public string? RequiredRuntime { get; init; }
    public string? EntryPath { get; init; }
}

public sealed class UpdatePackageMarker
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = string.Empty;
    public string Rid { get; init; } = string.Empty;
    public string PackageKind { get; init; } = string.Empty;
    public string RuntimeKind { get; init; } = string.Empty;
}
