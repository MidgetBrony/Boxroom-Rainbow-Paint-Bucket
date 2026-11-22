using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using ModsPanel;
using SteamShelf;
using SteamShelf.Input;
using SteamShelf.Items;
using SteamShelf.Materials;
using SteamShelf.PlayerTools;
using SteamShelf.UI;
using UnityEngine;

[assembly: MelonInfo(typeof(RainbowPaintBucket.Core), "Rainbow Paint Bucket", "0.1.11", "MidgetBrony")]
[assembly: MelonGame("NestedLoop", "BOXROOM")]
[assembly: MelonAdditionalDependencies("ModsPanel")]

namespace RainbowPaintBucket;

public sealed class Core : MelonMod
{
    internal const string MaterialIdPrefix = "RainbowPaintBucket_";
    internal const string DefaultHex = "#FF4FA3";
    private const string OwnerId = "MidgetBrony.RainbowPaintBucket";

    internal static Core Instance;
    internal static MaterialData BucketData;

    private static readonly Dictionary<string, Material> Materials =
        new(StringComparer.OrdinalIgnoreCase);
    private static Texture2D whiteTexture;
    private static Texture2D iconTexture;
    private static Sprite iconSprite;
    private static Color[] nativeIconPixels;
    private static Color nativeIconBackground;
    private static bool nativeIconLoaded;
    private static bool nativeIconLoading;
    private static MaterialsManager registeredManager;

    private readonly List<FavoriteColor> favorites = new();
    private string favoriteName = string.Empty;
    private ModMenu favoritesMenu;
    private Texture2D chooserSwatchTexture;
    private bool nativePickerOpen;

    internal bool ChooserOpen => nativePickerOpen || favoritesMenu?.IsOpen == true;
    internal MaterialSwapper ActiveSwapper { get; set; }
    internal string CurrentHex { get; private set; } = DefaultHex;

    private string FavoritesPath => Path.Combine(
        MelonEnvironment.UserDataDirectory, "RainbowPaintBucket", "favorites.json");

    public override void OnInitializeMelon()
    {
        Instance = this;
        LoadFavorites();
        CreateBucketData();
        LoggerInstance.Msg("Rainbow Paint Bucket 0.1.11 initialized. Right-click opens BOXROOM's RGB picker; Shift + right-click manages favorites.");
    }

    public override void OnUpdate()
    {
        RegisterLive();
        TryLoadNativePaintIcon();
        if (ChooserOpen && !IsBucketActive(ActiveSwapper))
        {
            CloseChooser();
        }
    }

    public override void OnDeinitializeMelon()
    {
        CloseChooser();
        foreach (Material material in Materials.Values)
        {
            if (material != null) UnityEngine.Object.Destroy(material);
        }
        Materials.Clear();
        if (iconSprite != null) UnityEngine.Object.Destroy(iconSprite);
        if (iconTexture != null) UnityEngine.Object.Destroy(iconTexture);
        if (whiteTexture != null) UnityEngine.Object.Destroy(whiteTexture);
        iconSprite = null;
        iconTexture = null;
        nativeIconPixels = null;
        nativeIconLoaded = false;
        nativeIconLoading = false;
        whiteTexture = null;
        if (BucketData != null) UnityEngine.Object.Destroy(BucketData);
        BucketData = null;
        Instance = null;
    }

    internal void OpenChooser(MaterialSwapper swapper, bool manageFavorites)
    {
        if (!IsBucketActive(swapper)) return;
        ActiveSwapper = swapper;
        favoriteName = string.Empty;
        if (manageFavorites) ShowFavoritesMenu();
        else OpenNativePicker();
    }

    internal void CloseChooser()
    {
        ModMenu menu = favoritesMenu;
        favoritesMenu = null;
        if (menu?.IsOpen == true) menu.Close();
        DestroyChooserSwatch();

        if (nativePickerOpen)
        {
            FinalizeNativeColor();
            nativePickerOpen = false;
            if (Singleton<MenuManager>.HasInstance() &&
                string.Equals(Singleton<MenuManager>.Instance.CurrentMenuId, "LightColour", StringComparison.Ordinal))
            {
                Singleton<MenuManager>.Instance.Back();
            }
        }
    }

    private void OpenNativePicker()
    {
        if (!Singleton<MenuManager>.HasInstance())
        {
            ModsUi.ShowToast("BOXROOM's RGB picker is not ready yet.");
            return;
        }

        TryNormalizeHex(CurrentHex, out _, out Color currentColor);
        nativePickerOpen = true;
        Singleton<MenuManager>.Instance.OpenMenu("LightColour", null, currentColor);
    }

    private void ShowFavoritesMenu()
    {
        DestroyChooserSwatch();
        chooserSwatchTexture = CreateChooserSwatch(CurrentHex);

        ModMenu menu = ModsUi.CreateMenu(
            OwnerId,
            "Rainbow Paint Favorites",
            "Save the current paint color with a name, or choose a color you saved earlier.");
        menu.Eyebrow = "NAMED COLORS";
        menu.CloseText = "RETURN TO PAINTING";
        menu.CompactWindow = true;
        menu.Draggable = true;
        menu.Closed = () =>
        {
            if (ReferenceEquals(favoritesMenu, menu))
            {
                favoritesMenu = null;
                DestroyChooserSwatch();
            }
        };

        menu.AddImage(() => chooserSwatchTexture, 105f)
            .AddLabel("Current paint color: " + CurrentHex)
            .AddButton("Open BOXROOM RGB picker", OpenNativePickerFromFavorites, "Choose a different RGB, HSV, or hex color")
            .AddHeading("Save a named favorite")
            .AddTextInput("Favorite name", () => favoriteName, value => favoriteName = value, "e.g. Arcade Pink")
            .AddButton("Save current color", SaveFavoriteFromMenu, "Store " + CurrentHex + " with this name")
            .AddHeading($"Favorite colors ({favorites.Count})");

        if (favorites.Count == 0)
        {
            menu.AddLabel("No favorites saved yet.");
        }
        foreach (FavoriteColor stored in favorites.ToArray())
        {
            FavoriteColor favorite = stored;
            menu.AddColorDeleteRow(
                favorite.Name,
                () => FavoriteColorValue(favorite),
                () => CommitColor(favorite.Hex),
                () => RemoveFavorite(favorite),
                favorite.Hex);
        }

        ModMenu oldMenu = favoritesMenu;
        favoritesMenu = menu;
        if (oldMenu != null) oldMenu.Closed = null;
        menu.Show();
    }

    private void OpenNativePickerFromFavorites()
    {
        ModMenu menu = favoritesMenu;
        favoritesMenu = null;
        if (menu?.IsOpen == true) menu.Close();
        DestroyChooserSwatch();
        OpenNativePicker();
    }

    private void CommitColor(string normalized)
    {
        if (!TryNormalizeHex(normalized, out normalized, out _))
        {
            ModsUi.ShowToast("Enter a six-digit hex color such as #FF4FA3.");
            return;
        }

        SetCurrentColor(normalized);
        CloseChooser();
        RefreshPaintBrush();
        ModsUi.ShowToast("Paint color set to " + normalized + ".");
        LoggerInstance.Msg("Selected paint color " + normalized + ".");
    }

    internal void NativeColorChanged(Color color)
    {
        if (!nativePickerOpen) return;
        SetCurrentColor("#" + ColorUtility.ToHtmlStringRGB(color));
    }

    internal void NativePickerClosed()
    {
        if (!nativePickerOpen) return;
        FinalizeNativeColor();
        nativePickerOpen = false;
    }

    private void FinalizeNativeColor()
    {
        GetMaterial(CurrentHex);
        RefreshPaintBrush();
        LoggerInstance.Msg("Selected paint color " + CurrentHex + " through BOXROOM's RGB picker.");
    }

    private void SetCurrentColor(string normalized)
    {
        if (!TryNormalizeHex(normalized, out normalized, out _)) return;
        CurrentHex = normalized;
        UpdateIcon(normalized);
        BucketData.ID = EncodeId(normalized);
        BucketData.DisplayDescription = $"Paint walls, ceilings, floors and furniture with {normalized}. Right-click opens BOXROOM's RGB picker; Shift + right-click manages favorites.";
    }

    private void RefreshPaintBrush()
    {
        MaterialSwapper swapper = ActiveSwapper;
        if (swapper != null)
        {
            try
            {
                AccessTools.Method(typeof(MaterialSwapper), "SetupPaintBrush")?.Invoke(swapper, null);
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning("Could not refresh the paint-bucket preview: " + ex.Message);
            }
        }
    }

    private void SaveFavoriteFromMenu()
    {
        string normalized = CurrentHex;
        string name = favoriteName;
        if (string.IsNullOrWhiteSpace(name))
        {
            ModsUi.ShowToast("Give this favorite a name first.");
            return;
        }

        name = name.Trim();
        int existing = favorites.FindIndex(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        FavoriteColor value = new() { Name = name, Hex = normalized };
        if (existing >= 0) favorites[existing] = value;
        else favorites.Add(value);
        favorites.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        SaveFavorites();
        favoriteName = string.Empty;
        ModsUi.ShowToast(existing >= 0 ? $"Updated favorite {name}." : $"Saved favorite {name}.");
        ShowFavoritesMenu();
    }

    private void RemoveFavorite(FavoriteColor favorite)
    {
        if (favorite == null || !favorites.Remove(favorite)) return;
        SaveFavorites();
        ModsUi.ShowToast("Removed favorite " + favorite.Name + ".");
        ShowFavoritesMenu();
    }

    private static Color FavoriteColorValue(FavoriteColor favorite)
    {
        return favorite != null && TryNormalizeHex(favorite.Hex, out _, out Color color)
            ? color
            : Color.white;
    }

    private void LoadFavorites()
    {
        favorites.Clear();
        try
        {
            if (!File.Exists(FavoritesPath)) return;
            string json = File.ReadAllText(FavoritesPath);
            MatchCollection matches = Regex.Matches(
                json,
                "\\{\\s*\\\"nameBase64\\\"\\s*:\\s*\\\"(?<name>[^\\\"]*)\\\"\\s*,\\s*\\\"hex\\\"\\s*:\\s*\\\"(?<hex>#[0-9A-Fa-f]{6})\\\"\\s*\\}");
            foreach (Match match in matches)
            {
                string name;
                try
                {
                    name = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["name"].Value));
                }
                catch
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!TryNormalizeHex(match.Groups["hex"].Value, out string normalized, out _)) continue;
                favorites.Add(new FavoriteColor { Name = name.Trim(), Hex = normalized });
            }
            favorites.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning("Could not load favorite colors: " + ex.Message);
        }
    }

    private void SaveFavorites()
    {
        try
        {
            string directory = Path.GetDirectoryName(FavoritesPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            StringBuilder json = new();
            json.AppendLine("{");
            json.AppendLine("  \"colors\": [");
            for (int i = 0; i < favorites.Count; i++)
            {
                FavoriteColor favorite = favorites[i];
                string encodedName = Convert.ToBase64String(Encoding.UTF8.GetBytes(favorite.Name));
                json.Append("    { \"nameBase64\": \"").Append(encodedName)
                    .Append("\", \"hex\": \"").Append(favorite.Hex).Append("\" }");
                if (i < favorites.Count - 1) json.Append(',');
                json.AppendLine();
            }
            json.AppendLine("  ]");
            json.AppendLine("}");

            string temporaryPath = FavoritesPath + ".tmp";
            string backupPath = FavoritesPath + ".bak";
            File.WriteAllText(temporaryPath, json.ToString());
            if (File.Exists(FavoritesPath))
                File.Replace(temporaryPath, FavoritesPath, backupPath, true);
            else
                File.Move(temporaryPath, FavoritesPath);
        }
        catch (Exception ex)
        {
            LoggerInstance.Warning("Could not save favorite colors: " + ex.Message);
        }
    }

    private static Texture2D CreateChooserSwatch(string value)
    {
        const int width = 256;
        const int height = 64;
        Texture2D texture = new(width, height, TextureFormat.RGBA32, false)
        {
            name = "Rainbow Paint Bucket Color Preview",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        UpdateChooserSwatch(texture, value);
        return texture;
    }

    private static void UpdateChooserSwatch(Texture2D texture, string value)
    {
        if (texture == null) return;
        bool valid = TryNormalizeHex(value, out _, out Color color);
        if (!valid) color = new Color(0.22f, 0.22f, 0.22f, 1f);
        int width = texture.width;
        int height = texture.height;
        Color[] pixels = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool border = x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
                pixels[y * width + x] = border ? new Color(0.13f, 0.24f, 0.25f, 1f) : color;
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, false);
    }

    private void DestroyChooserSwatch()
    {
        if (chooserSwatchTexture != null) UnityEngine.Object.Destroy(chooserSwatchTexture);
        chooserSwatchTexture = null;
    }

    private static void CreateBucketData()
    {
        CreateIcon();
        BucketData = ScriptableObject.CreateInstance<MaterialData>();
        BucketData.name = "Rainbow Paint Bucket";
        BucketData.ID = EncodeId(DefaultHex);
        BucketData.DisplayName = "Rainbow Paint Bucket";
        BucketData.DisplayDescription = "Paint any surface with a custom color. Right-click opens BOXROOM's RGB picker; Shift + right-click manages named favorites.";
        BucketData.IsLoadedFromMod = true;
        BucketData.SurfaceType = MaterialData.MaterialSurfaceType.WallPaint |
            MaterialData.MaterialSurfaceType.FurniturePaint;
    }

    internal static void RegisterLive()
    {
        if (BucketData == null || !Singleton<MaterialsManager>.HasInstance()) return;
        MaterialsManager manager = Singleton<MaterialsManager>.Instance;
        List<MaterialData> data = manager.MaterialDatas;
        if (data == null || data.Any(item => item == BucketData || IsBucket(item))) return;
        data.Insert(0, BucketData);
        if (registeredManager != manager)
        {
            registeredManager = manager;
            Instance?.LoggerInstance.Msg($"Registered Rainbow Paint Bucket in BOXROOM's material list ({data.Count} total materials).");
        }
    }

    internal static bool IsBucket(MaterialData data) =>
        data != null && (ReferenceEquals(data, BucketData) || IsColorId(data.ID));

    internal static bool IsBucketActive(MaterialSwapper swapper)
    {
        if (swapper == null) return false;
        try
        {
            PlayerToolController controller = AccessTools.Field(typeof(PlayerTool), "controller")?.GetValue(swapper) as PlayerToolController;
            return IsBucket(controller?.CurrentItem as MaterialData);
        }
        catch
        {
            return false;
        }
    }

    internal static string EncodeId(string hex) => MaterialIdPrefix + hex.TrimStart('#').ToUpperInvariant();

    internal static bool IsColorId(string id) =>
        !string.IsNullOrEmpty(id) && id.StartsWith(MaterialIdPrefix, StringComparison.OrdinalIgnoreCase) &&
        TryNormalizeHex(id.Substring(MaterialIdPrefix.Length), out _, out _);

    internal static bool TryGetHexFromId(string id, out string hex)
    {
        hex = null;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(MaterialIdPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        return TryNormalizeHex(id.Substring(MaterialIdPrefix.Length), out hex, out _);
    }

    internal static bool TryNormalizeHex(string text, out string normalized, out Color color)
    {
        normalized = null;
        color = Color.white;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim();
        if (!value.StartsWith("#", StringComparison.Ordinal)) value = "#" + value;
        if (value.Length != 7 || !ColorUtility.TryParseHtmlString(value, out color)) return false;
        normalized = value.ToUpperInvariant();
        return true;
    }

    internal static Material GetMaterial(string hex)
    {
        if (!TryNormalizeHex(hex, out string normalized, out Color color)) return null;
        if (Materials.TryGetValue(normalized, out Material cached) && cached != null) return cached;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        if (shader == null) return null;
        Material material = new(shader)
        {
            name = "Rainbow Paint " + normalized,
            color = color,
            mainTexture = GetWhiteTexture()
        };
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.18f);
        Materials[normalized] = material;
        return material;
    }

    internal static async Task<SpriteAndColor> GetSpriteAndColor()
    {
        await EnsureNativePaintIcon();
        return new SpriteAndColor { Sprite = iconSprite, Color = Color.white };
    }

    private static Texture2D GetWhiteTexture()
    {
        if (whiteTexture != null) return whiteTexture;
        whiteTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = "Rainbow Paint Bucket White Texture",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        whiteTexture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
        whiteTexture.Apply(false, true);
        return whiteTexture;
    }

    private static void CreateIcon()
    {
        iconTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = "Rainbow Paint Bucket Icon Fallback",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        iconSprite = Sprite.Create(iconTexture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 100f);
        iconSprite.name = "Rainbow Paint Bucket Icon";
        UpdateIcon(DefaultHex);
    }

    private static void UpdateIcon(string hex)
    {
        if (iconTexture == null) return;
        TryNormalizeHex(hex, out _, out Color selected);
        if (!nativeIconLoaded || nativeIconPixels == null)
        {
            iconTexture.SetPixels(new[] { selected, selected, selected, selected });
            iconTexture.Apply(false, false);
            return;
        }

        Color[] pixels = new Color[nativeIconPixels.Length];
        float baseBrightness = Mathf.Max(0.01f, (nativeIconBackground.r + nativeIconBackground.g + nativeIconBackground.b) / 3f);
        for (int i = 0; i < nativeIconPixels.Length; i++)
        {
            Color source = nativeIconPixels[i];
            float dr = source.r - nativeIconBackground.r;
            float dg = source.g - nativeIconBackground.g;
            float db = source.b - nativeIconBackground.b;
            if (source.a > 0.01f && dr * dr + dg * dg + db * db < 0.055f)
            {
                float brightness = ((source.r + source.g + source.b) / 3f) / baseBrightness;
                source.r = Mathf.Clamp01(selected.r * brightness);
                source.g = Mathf.Clamp01(selected.g * brightness);
                source.b = Mathf.Clamp01(selected.b * brightness);
            }
            pixels[i] = source;
        }
        iconTexture.SetPixels(pixels);
        iconTexture.Apply(false, false);
    }

    private static async Task EnsureNativePaintIcon()
    {
        if (nativeIconLoaded || !Singleton<MaterialsManager>.HasInstance()) return;
        MaterialsManager manager = Singleton<MaterialsManager>.Instance;
        if (manager.MaterialDatas == null || manager.MaterialDatas.Count <= 1) return;
        MaterialData sourceData = manager.MaterialDatas.FirstOrDefault(data =>
            data != null && !data.IsLoadedFromMod &&
            data.SurfaceType == MaterialData.MaterialSurfaceType.WallPaint &&
            data.SpriteReference != null && data.SpriteReference.RuntimeKeyIsValid());
        sourceData ??= manager.MaterialDatas.FirstOrDefault(data =>
            data != null && !data.IsLoadedFromMod &&
            (data.SurfaceType & MaterialData.MaterialSurfaceType.WallPaint) != 0 &&
            data.SpriteReference != null && data.SpriteReference.RuntimeKeyIsValid());
        if (sourceData == null) return;

        SpriteAndColor sourceIcon = await manager.GetSprite(sourceData);
        Sprite sourceSprite = sourceIcon?.Sprite;
        if (sourceSprite == null || sourceSprite.texture == null) return;

        Texture sourceTexture = sourceSprite.texture;
        RenderTexture temporary = RenderTexture.GetTemporary(sourceTexture.width, sourceTexture.height, 0, RenderTextureFormat.ARGB32);
        RenderTexture previous = RenderTexture.active;
        try
        {
            Graphics.Blit(sourceTexture, temporary);
            RenderTexture.active = temporary;
            Texture2D readable = new(sourceTexture.width, sourceTexture.height, TextureFormat.RGBA32, false)
            {
                name = "Rainbow Paint Bucket Native Icon Copy",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            readable.ReadPixels(new Rect(0f, 0f, sourceTexture.width, sourceTexture.height), 0, 0, false);
            readable.Apply(false, false);

            if (iconSprite != null) UnityEngine.Object.Destroy(iconSprite);
            if (iconTexture != null) UnityEngine.Object.Destroy(iconTexture);
            iconTexture = readable;
            Rect spriteRect = sourceSprite.rect;
            Vector2 pivot = new(
                sourceSprite.pivot.x / Mathf.Max(1f, spriteRect.width),
                sourceSprite.pivot.y / Mathf.Max(1f, spriteRect.height));
            iconSprite = Sprite.Create(iconTexture, spriteRect, pivot, sourceSprite.pixelsPerUnit);
            iconSprite.name = "Rainbow Paint Bucket Native Paint Icon";
            nativeIconPixels = iconTexture.GetPixels();
            nativeIconBackground = FindDominantIconColor(nativeIconPixels);
            nativeIconLoaded = true;
            UpdateIcon(Instance?.CurrentHex ?? DefaultHex);
            Instance?.LoggerInstance.Msg("Loaded native BOXROOM paint icon from " + sourceData.name + ".");
            RefreshVisibleItemIcons();
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private static async void TryLoadNativePaintIcon()
    {
        if (nativeIconLoaded || nativeIconLoading || !Singleton<MaterialsManager>.HasInstance()) return;
        MaterialsManager manager = Singleton<MaterialsManager>.Instance;
        if (manager.MaterialDatas == null || manager.MaterialDatas.Count <= 1) return;
        nativeIconLoading = true;
        try
        {
            await EnsureNativePaintIcon();
        }
        catch (Exception ex)
        {
            Instance?.LoggerInstance.Warning("Could not load BOXROOM's native paint icon: " + ex.Message);
        }
        finally
        {
            nativeIconLoading = false;
        }
    }

    private static void RefreshVisibleItemIcons()
    {
        foreach (UIToolbarItem slot in Resources.FindObjectsOfTypeAll<UIToolbarItem>())
        {
            if (slot != null && IsBucket(slot.Item as MaterialData)) slot.RefreshFromData(slot.Item);
        }
        foreach (ItemUIButton button in Resources.FindObjectsOfTypeAll<ItemUIButton>())
        {
            if (button == null || !IsBucket(button.ItemData as MaterialData)) continue;
            AccessTools.Method(typeof(ItemUIButton), "SetSprite")?.Invoke(button, null);
        }
    }

    private static Color FindDominantIconColor(Color[] pixels)
    {
        Dictionary<int, int> counts = new();
        foreach (Color color in pixels)
        {
            if (color.a < 0.5f) continue;
            int r = Mathf.Clamp(Mathf.RoundToInt(color.r * 15f), 0, 15);
            int g = Mathf.Clamp(Mathf.RoundToInt(color.g * 15f), 0, 15);
            int b = Mathf.Clamp(Mathf.RoundToInt(color.b * 15f), 0, 15);
            int key = (r << 8) | (g << 4) | b;
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }
        if (counts.Count == 0) return Color.white;
        int dominant = counts.OrderByDescending(pair => pair.Value).First().Key;
        return new Color(((dominant >> 8) & 15) / 15f, ((dominant >> 4) & 15) / 15f, (dominant & 15) / 15f, 1f);
    }
}

[HarmonyPatch(typeof(CatalogueData), nameof(CatalogueData.GenerateModsPages))]
internal static class RegisterBucketWhenCatalogueBuildsPatch
{
    private static bool catalogueResultLogged;

    private static void Prefix() => Core.RegisterLive();

    private static void Postfix(CatalogueData __instance, ref List<CataloguePageDefinition> __result)
    {
        if (Core.BucketData == null || __instance == null) return;
        __result ??= new List<CataloguePageDefinition>();

        bool alreadyPresent = __result.Any(page => page?.Items != null &&
            page.Items.Any(item => ReferenceEquals(item, Core.BucketData) || Core.IsBucket(item as MaterialData)));

        if (!alreadyPresent)
        {
            CataloguePageDefinition page = __result.LastOrDefault(value => value != null && value.Items != null);
            int capacity = __instance.ModsItemsPerPage;
            if (page != null && (capacity <= 0 || page.Items.Length < capacity))
            {
                Item[] items = new Item[page.Items.Length + 1];
                Array.Copy(page.Items, items, page.Items.Length);
                items[items.Length - 1] = Core.BucketData;
                page.FillItems(items);
            }
            else if (__instance.Pages != null && __instance.Pages.Length > 0 && __instance.Pages[0] != null)
            {
                CataloguePageDefinition template = __instance.Pages[0];
                CataloguePageDefinition newPage = ScriptableObject.CreateInstance<CataloguePageDefinition>();
                newPage.PageTemplate = template.PageTemplate;
                newPage.PageName = template.PageName;
                newPage.LocalizedPageName = template.LocalizedPageName;
                newPage.FillItems(new Item[] { Core.BucketData });
                __result.Add(newPage);
            }
        }

        if (!catalogueResultLogged)
        {
            catalogueResultLogged = true;
            int itemCount = __result.Sum(page => page?.Items?.Length ?? 0);
            Core.Instance?.LoggerInstance.Msg($"Mods catalogue generated with Rainbow Paint Bucket present={(!alreadyPresent ? "injected" : "native")}; {__result.Count} pages, {itemCount} items.");
        }
    }
}

[Serializable]
internal sealed class FavoriteColor
{
    public string Name;
    public string Hex;
}

[HarmonyPatch(typeof(ModsMaterialLoader), nameof(ModsMaterialLoader.LoadMaterialData))]
internal static class AddBucketToMaterialsPatch
{
    private static void Postfix(ref Task<List<MaterialData>> __result) => __result = Append(__result);

    private static async Task<List<MaterialData>> Append(Task<List<MaterialData>> original)
    {
        List<MaterialData> result = await original ?? new List<MaterialData>();
        if (Core.BucketData != null && result.All(item => item != Core.BucketData && !Core.IsBucket(item)))
            result.Insert(0, Core.BucketData);
        return result;
    }
}

[HarmonyPatch(typeof(MaterialsManager), nameof(MaterialsManager.GetMaterial))]
internal static class LoadBucketMaterialPatch
{
    private static bool Prefix(MaterialData data, ref Task<Material> __result)
    {
        if (!Core.IsBucket(data)) return true;
        string hex = Core.TryGetHexFromId(data.ID, out string parsed) ? parsed : Core.Instance?.CurrentHex ?? Core.DefaultHex;
        __result = Task.FromResult(Core.GetMaterial(hex));
        return false;
    }
}

[HarmonyPatch(typeof(MaterialsManager), nameof(MaterialsManager.GetMaterialByID))]
internal static class RestoreSavedBucketMaterialPatch
{
    private static bool Prefix(string id, ref Task<Material> __result)
    {
        if (!Core.TryGetHexFromId(id, out string hex)) return true;
        __result = Task.FromResult(Core.GetMaterial(hex));
        return false;
    }
}

[HarmonyPatch(typeof(MaterialsManager), nameof(MaterialsManager.GetSprite))]
internal static class BucketSpritePatch
{
    private static bool Prefix(MaterialData data, ref Task<SpriteAndColor> __result)
    {
        if (!Core.IsBucket(data)) return true;
        __result = Core.GetSpriteAndColor();
        return false;
    }
}

[HarmonyPatch(typeof(MaterialSwapper), nameof(MaterialSwapper.OnUpdate))]
internal static class BucketInputPatch
{
    private static bool Prefix(MaterialSwapper __instance, PlayerInputContext inputContext)
    {
        if (!Core.IsBucketActive(__instance))
        {
            if (Core.Instance?.ChooserOpen == true) Core.Instance.CloseChooser();
            return true;
        }

        Core.Instance.ActiveSwapper = __instance;
        if (Core.Instance.ChooserOpen) return false;
        if (inputContext != null && inputContext.SecondaryPressedThisFrame)
        {
            Core.Instance.OpenChooser(__instance, inputContext.PlacementModifierPressed);
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Menu_LightColour), "OnColourChanged")]
internal static class NativeRgbPickerColorPatch
{
    private static void Postfix(Color color) => Core.Instance?.NativeColorChanged(color);
}

[HarmonyPatch(typeof(Menu_LightColour), "OnPreHide")]
internal static class NativeRgbPickerClosePatch
{
    private static void Prefix() => Core.Instance?.NativePickerClosed();
}

[HarmonyPatch(typeof(MaterialSwapper), nameof(MaterialSwapper.OnToolDeactivated))]
internal static class CloseBucketChooserPatch
{
    private static void Postfix(MaterialSwapper __instance)
    {
        if (Core.Instance?.ActiveSwapper == __instance)
        {
            Core.Instance.CloseChooser();
            Core.Instance.ActiveSwapper = null;
        }
    }
}
