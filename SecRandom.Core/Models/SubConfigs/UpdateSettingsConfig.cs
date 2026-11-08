using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using SecRandom.Shared.Updates;

namespace SecRandom.Core.Models.SubConfigs;

public partial class UpdateSettingsConfig : ObservableObject, IJsonOnDeserialized
{
    private static readonly DateOnly LegacyUnsetLastCheckDate = new(1970, 1, 1);
    [JsonIgnore] private bool _updateModeVersionLoaded;

    [ObservableProperty] private int _autoUpdateMode = 3;
    [ObservableProperty] private int _updateModeVersion = 1;
    [ObservableProperty] private UpdateChannel _updateChannel = UpdateChannel.Release;

    // custom-only: 特供构建的取源策略；AUTO 先直连，超时或失败后按 MirrorPrefixes 顺序降级
    [ObservableProperty] private UpdateSourceMode _sourceMode = UpdateSourceMode.Auto;

    /// <summary>
    /// 镜像前缀，多个用逗号分隔，拼在完整 GitHub 地址之前。留空表示使用 <see cref="BuiltInMirrorPrefixes"/>。
    /// custom-only：前缀属于配置而不是业务逻辑里的常量，方便校园网环境替换可用镜像。
    /// </summary>
    [ObservableProperty] private string _mirrorPrefixes = BuiltInMirrorPrefixes;

    public const string BuiltInMirrorPrefixes = "https://gh-proxy.com/,https://ghfast.top/,https://ghproxy.net/,https://hub.gitmirror.com/";

    [ObservableProperty]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private DateTime? _lastCheckTime;

    partial void OnLastCheckTimeChanged(DateTime? value)
    {
        if (value.HasValue && DateOnly.FromDateTime(value.Value) == LegacyUnsetLastCheckDate)
            LastCheckTime = null;
    }

    partial void OnUpdateModeVersionChanged(int value) => _updateModeVersionLoaded = true;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!_updateModeVersionLoaded)
        {
            // Old nonzero values meant scheduled checks; retain that behavior without upgrading users to auto-install.
            AutoUpdateMode = AutoUpdateMode == 0 ? 0 : 1;
            UpdateModeVersion = 1;
        }
    }
}
