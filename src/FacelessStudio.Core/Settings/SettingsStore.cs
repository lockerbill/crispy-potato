using System.Text.Json;

namespace FacelessStudio.Core.Settings;

/// <summary>Ảnh chụp cấu hình tại một thời điểm (giá trị đã lưu &gt; biến môi trường &gt; mặc định).</summary>
public sealed class AppSettings
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public AppSettings(IReadOnlyDictionary<string, string> values, string baseDir)
    {
        _values = values;
        BaseDir = baseDir;
    }

    /// <summary>Thư mục gốc để giải các đường dẫn tương đối (data/, assets/).</summary>
    public string BaseDir { get; }

    public string Get(string key)
    {
        if (_values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        return SettingCatalog.Find(key)?.Default ?? "";
    }

    public bool Has(string key) => !string.IsNullOrWhiteSpace(Get(key));

    /// <summary>Giải đường dẫn tương đối theo BaseDir; giữ nguyên tên lệnh trần như "ffmpeg".</summary>
    public string ResolvePath(string key)
    {
        var value = Get(key);
        if (string.IsNullOrEmpty(value) || Path.IsPathRooted(value)) return value;
        if (!value.Contains('/') && !value.Contains('\\')) return value;
        return Path.GetFullPath(Path.Combine(BaseDir, value));
    }

    public static AppSettings Empty(string baseDir = ".") => new(new Dictionary<string, string>(), baseDir);
}

/// <summary>Lưu cấu hình (API key, endpoint...) vào data/settings.json trên máy.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _file;
    private readonly Lock _lock = new();
    private Dictionary<string, string> _values;

    public SettingsStore(string baseDir)
    {
        BaseDir = Path.GetFullPath(baseDir);
        DataDir = Path.Combine(BaseDir, "data");
        _file = Path.Combine(DataDir, "settings.json");
        _values = Load();
    }

    public string BaseDir { get; }
    public string DataDir { get; }

    public event Action? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_lock) return new AppSettings(new Dictionary<string, string>(_values), BaseDir);
        }
    }

    public string GetStored(string key)
    {
        lock (_lock) return _values.GetValueOrDefault(key, "");
    }

    public void Save(IDictionary<string, string> values)
    {
        lock (_lock)
        {
            _values = values
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
                .ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(_file, JsonSerializer.Serialize(_values, JsonOptions));
        }
        Changed?.Invoke();
    }

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(_file)) return new();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
