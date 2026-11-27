using Godot;
using Voracity.Core.Mapping;
using Voracity.Core.Models;

namespace Voracity.Client.Map;

/// <summary>
/// Manages baked raster uploads (province_data_tex, height_coast_tex, 256x768 RGBA8 lookup palette)
/// and binary .vmap cache persistence under the user data directory.
/// </summary>
public sealed class MapTextureController
{
    public const int LookupWidth = 256;
    public const int LookupHeight = 768; // 256 banks x 3 rows = 65,536 Location ID capacity

    private ImageTexture? _provinceDataTex;
    private ImageTexture? _heightCoastTex;
    private Image? _lookupImage;
    private ImageTexture? _lookupTex;

    public BakedMapArtifact? CurrentBaked { get; private set; }
    public PlanarMapTopology? CurrentTopology { get; private set; }
    public Texture2D? ProvinceDataTexture => _provinceDataTex;
    public Texture2D? HeightCoastTexture => _heightCoastTex;
    public Texture2D? LookupTexture => _lookupTex;

    public void Reset()
    {
        CurrentBaked = null;
        CurrentTopology = null;
    }

    public BakedMapArtifact BakeAndUpload(
        IReadOnlyList<ProvinceSnapshot> locations,
        GeographyFeaturesData features,
        PlanarMapTopology? prebuiltTopology,
        string? vmapCacheDir,
        bool bypassCache)
    {
        CurrentTopology = prebuiltTopology ?? PlanarTopologyBuilder.BuildFromProvinces(locations, features);
        var hash = MapBakePipeline.ComputeTopologyHash(CurrentTopology, features, MapBakePipeline.DefaultWidth, MapBakePipeline.DefaultHeight);
        var cachePath = !bypassCache && !string.IsNullOrWhiteSpace(vmapCacheDir)
            ? Path.Combine(vmapCacheDir, $"pontaria_{hash[..12]}.vmap")
            : null;

        var artifact = cachePath is not null ? MapBakePipeline.TryLoadVmapCache(cachePath, hash) : null;
        if (artifact is null)
        {
            artifact = MapBakePipeline.BakeFromTopology(CurrentTopology, locations, features);
            if (cachePath is not null)
                MapBakePipeline.SaveVmapCache(cachePath, artifact);
        }

        CurrentBaked = artifact;

        var pImg = Image.CreateFromData(artifact.Width, artifact.Height, false, Image.Format.Rgb8, artifact.ProvinceDataRgb);
        _provinceDataTex = ImageTexture.CreateFromImage(pImg);

        var hImg = Image.CreateFromData(artifact.Width, artifact.Height, false, Image.Format.Rgb8, artifact.HeightCoastRgb);
        _heightCoastTex = ImageTexture.CreateFromImage(hImg);

        if (_lookupImage is null || _lookupTex is null)
        {
            _lookupImage = Image.CreateEmpty(LookupWidth, LookupHeight, false, Image.Format.Rgba8);
            _lookupTex = ImageTexture.CreateFromImage(_lookupImage);
        }

        return artifact;
    }

    public void SetPrebuiltTopology(PlanarMapTopology? topology)
    {
        CurrentTopology = topology;
    }

    public void UploadFullLookup(
        RenderSnapshot snapshot,
        MapLensMode lens,
        IReadOnlyDictionary<string, StateSnapshot> stateById,
        IReadOnlyDictionary<string, int> nationTagById,
        IReadOnlyDictionary<string, int> stateTagById,
        IReadOnlyDictionary<string, Color> marketColors)
    {
        if (_lookupImage is null || _lookupTex is null)
            return;

        _lookupImage.SetPixel(0, 0, new Color(0.08f, 0.13f, 0.19f, 1.0f));
        _lookupImage.SetPixel(0, 1, new Color(0f, 0f, 0f, 0f));
        _lookupImage.SetPixel(0, 2, new Color(0f, 0f, 0f, 1.0f));

        for (var i = 0; i < snapshot.Provinces.Count; i++)
        {
            WriteLocationEntry(snapshot.Provinces[i], lens, stateById, nationTagById, stateTagById, marketColors);
        }

        _lookupTex.Update(_lookupImage);
    }

    public void WriteLocationEntry(
        ProvinceSnapshot loc,
        MapLensMode lens,
        IReadOnlyDictionary<string, StateSnapshot> stateById,
        IReadOnlyDictionary<string, int> nationTagById,
        IReadOnlyDictionary<string, int> stateTagById,
        IReadOnlyDictionary<string, Color> marketColors)
    {
        if (_lookupImage is null || loc.NumericalIndex <= 0 || loc.NumericalIndex > 65535)
            return;

        var p = loc.NumericalIndex;
        var px = p & 255;
        var pyBase = (p >> 8) * 3;

        stateById.TryGetValue(loc.StateId, out var state);
        var lensCol = MapLensColorizer.ResolveLensColor(loc, state, lens, marketColors);

        var nationTag = loc.Kind == LocationKind.SeaZone ? 0 : nationTagById.GetValueOrDefault(loc.OwnerNationId, 1);
        var stateTag = loc.Kind == LocationKind.SeaZone ? 0 : stateTagById.GetValueOrDefault(loc.StateId, 0);
        var kindAlpha = MapLensColorizer.EncodeLocationKindAlpha(loc.Kind);

        _lookupImage.SetPixel(px, pyBase + 0, lensCol);
        _lookupImage.SetPixel(px, pyBase + 1, new Color(
            (nationTag & 255) / 255.0f,
            ((nationTag >> 8) & 255) / 255.0f,
            (stateTag & 255) / 255.0f,
            ((stateTag >> 8) & 255) / 255.0f));
        _lookupImage.SetPixel(px, pyBase + 2, new Color(0f, 0f, 0f, kindAlpha));
    }
}
