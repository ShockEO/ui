using System;
using System.IO;
using System.Text.Json;
using ShockUI.Models.Camera;

namespace ShockUI.Services.Camera;

public sealed class FovPresetService
{
    public sealed class ZGPositions
    {
        public uint ZG1 { get; set; }
        public uint ZG2 { get; set; }
        public uint ZG3 { get; set; }
    }

    public sealed class FOVPositions
    {
        public ZGPositions WFOV { get; set; } = new();
        public ZGPositions MWFOV { get; set; } = new();
        public ZGPositions MNFOV { get; set; } = new();
        public ZGPositions NFOV { get; set; } = new();
    }

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _basePath;

    public FovPresetService()
    {
        _basePath = AppContext.BaseDirectory;
    }

    public ZGPositions? GetPreset(CameraType cameraType, string fovName)
    {
        var all = LoadCameraPositions(cameraType);
        if (all is null)
            return null;

        return fovName switch
        {
            "WFOV" => all.WFOV,
            "MWFOV" => all.MWFOV,
            "MNFOV" => all.MNFOV,
            "NFOV" => all.NFOV,
            _ => null
        };
    }

    public void UpdatePreset(CameraType cameraType, string fovName, uint zg1, uint zg2, uint zg3)
    {
        var all = LoadCameraPositions(cameraType) ?? CreateDefault();

        var target = fovName switch
        {
            "WFOV" => all.WFOV,
            "MWFOV" => all.MWFOV,
            "MNFOV" => all.MNFOV,
            "NFOV" => all.NFOV,
            _ => null
        };

        if (target is null)
            return;

        target.ZG1 = zg1;
        target.ZG2 = zg2;
        target.ZG3 = zg3;

        SaveCameraPositions(cameraType, all);
    }

    public string GetFilePath(CameraType cameraType)
    {
        return Path.Combine(_basePath, GetFileName(cameraType));
    }

    public FOVPositions? LoadCameraPositions(CameraType cameraType)
    {
        try
        {
            string path = GetFilePath(cameraType);

            if (!File.Exists(path))
                return CreateDefault();

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FOVPositions>(json) ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void SaveCameraPositions(CameraType cameraType, FOVPositions positions)
    {
        string path = GetFilePath(cameraType);
        string json = JsonSerializer.Serialize(positions, _jsonOptions);
        File.WriteAllText(path, json);
    }

    private static string GetFileName(CameraType cameraType)
    {
        return cameraType switch
        {
            CameraType.VISNIR => "visnir_fov_positions.json",
            CameraType.SWIR => "swir_fov_positions.json",
            CameraType.MWIR => "mwir_fov_positions.json",
            _ => "visnir_fov_positions.json"
        };
    }

    private static FOVPositions CreateDefault()
    {
        return new FOVPositions
        {
            WFOV = new ZGPositions(),
            MWFOV = new ZGPositions(),
            MNFOV = new ZGPositions(),
            NFOV = new ZGPositions()
        };
    }
}