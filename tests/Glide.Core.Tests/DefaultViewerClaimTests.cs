using Glide.App.Platform;
using Facts = Glide.App.Platform.WindowsFileAssociationRegistration.DefaultClaimFacts;

namespace Glide.Core.Tests;

/// <summary>
/// Default-viewer claim policy and the Explorer thumbnail handler policy. Pure functions over fake registry
/// snapshots: nothing here reads or writes the real registry.
/// </summary>
// Shares the non-parallel collection with the tests that register codec providers in the process-wide
// CodecCapabilityRegistry, so the candidate list below is computed against a known registry state.
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Shell")]
public sealed class DefaultViewerClaimTests
{
    private const string Glide = "Glide.Image";
    private static Facts Unowned(string extension = ".heic") => new(extension, null, null, null, false, Array.Empty<string>());

    [Fact]
    public void Claims_an_extension_nothing_else_owns()
        => Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned()));

    [Fact]
    public void Claim_is_idempotent_when_the_value_is_already_glides()
    {
        Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { TargetHiveDefault = Glide }));
        Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { OpenWithProgIds = new[] { Glide } }));
        Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { UserChoiceProgId = Glide }));
        Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { OtherHiveDefault = Glide, OtherHiveDefaultResolves = true }));
    }

    [Fact]
    public void Never_claims_when_the_user_chose_another_app()
        => Assert.False(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { UserChoiceProgId = "AppX43hnxtbyyps62jhe9sqpdzxn1790zetc" }));

    [Fact]
    public void Never_overwrites_another_apps_default_value_in_the_hive_being_written()
    {
        Assert.False(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { TargetHiveDefault = "IrfanView.jpg" }));
        // Even a dangling value there is somebody else's: never overwritten.
        Assert.False(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { TargetHiveDefault = "Gone.ProgId" }));
    }

    [Fact]
    public void Never_claims_when_another_app_lists_itself_in_open_with()
        => Assert.False(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { OpenWithProgIds = new[] { Glide, "SumatraPDF.heic" } }));

    [Fact]
    public void Other_hive_default_blocks_the_claim_only_when_it_resolves_to_a_working_handler()
    {
        Assert.False(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { OtherHiveDefault = "Photoshop.CameraRawFileCanon2.140", OtherHiveDefaultResolves = true }));
        Assert.True(WindowsFileAssociationRegistration.ShouldClaimDefault(Unowned() with { OtherHiveDefault = "Uninstalled.App.1", OtherHiveDefaultResolves = false }));
    }

    [Fact]
    public void Unregister_releases_a_claim_only_while_the_default_is_still_glides()
    {
        Assert.True(WindowsFileAssociationRegistration.ShouldReleaseClaim(Glide));
        Assert.True(WindowsFileAssociationRegistration.ShouldReleaseClaim("glide.image"));
        Assert.False(WindowsFileAssociationRegistration.ShouldReleaseClaim("IrfanView.heic")); // another app took it since
        Assert.False(WindowsFileAssociationRegistration.ShouldReleaseClaim(null));
        Assert.False(WindowsFileAssociationRegistration.ShouldReleaseClaim(string.Empty));
    }

    [Fact]
    public void Only_formats_glide_can_open_are_ever_claimed()
    {
        // Earlier tests may have loaded the real codec module, which registers its suffixes as decodable;
        // this test describes a PC where only the bundled list passed in decides.
        global::Glide.Core.CodecCapabilityRegistry.ClearExternalProviders();
        var candidates = WindowsFileAssociationRegistration.DefaultClaimCandidates(BundledCodecLibraries.BundledExtensions);
        foreach (var extension in new[] { ".jpg", ".png", ".webp", ".heic", ".avif", ".jxl", ".jp2", ".cr2", ".dng", ".exr", ".psd", ".tga", ".svg", ".dds", ".jxr" })
            Assert.Contains(extension, candidates);
        // Recognised but not reliably openable on a clean PC, or not associable by Windows at all.
        foreach (var extension in new[] { ".pdf", ".dwg", ".cdr", ".skp", ".r3d", ".basis", ".nii.gz", ".fts.gz", ".hdr.gz", ".ome.tif" })
            Assert.DoesNotContain(extension, candidates);
        // Without the codec library its formats are not claimed either.
        Assert.DoesNotContain(".heic", WindowsFileAssociationRegistration.DefaultClaimCandidates(Array.Empty<string>()));
    }

    [Fact]
    public void Default_apps_page_targets_glides_registered_application()
    {
        Assert.Equal("ms-settings:defaultapps?registeredAppMachine=Glide", WindowsFileAssociationRegistration.DefaultAppsUri(perUser: false));
        Assert.Equal("ms-settings:defaultapps?registeredAppUser=Glide", WindowsFileAssociationRegistration.DefaultAppsUri(perUser: true));
        // The name must be the RegisteredApplications value Glide writes.
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Glide.App", "Platform", "WindowsFileAssociationRegistration.cs"));
        Assert.Contains("private const string ApplicationName = \"Glide\";", source);
        Assert.Contains("registered?.SetValue(ApplicationName, @\"Software\\Glide\\Capabilities\"", source);
    }

    // Source-text contract kept: the installer is Inno Setup Pascal and cannot run headlessly here.
    [Fact]
    public void Installer_registers_only_for_the_ticked_task_and_opens_default_apps_only_when_interactive()
    {
        var iss = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Glide.iss"));
        Assert.Contains("Name: \"integrate\"", iss);
        Assert.DoesNotContain("Flags: unchecked", iss.Split("[Tasks]")[1].Split("[Files]")[0]);
        Assert.Contains("if WizardIsTaskSelected('integrate') then", iss);
        Assert.Contains("if not WizardSilent then", iss);
        Assert.Contains("ms-settings:defaultapps?registeredAppMachine=Glide", iss);
        Assert.Contains("ShellExecAsOriginalUser", iss);
    }

    [Fact]
    public void Installer_thumbnails_task_registers_or_unregisters_and_refreshes_as_the_original_user()
    {
        var iss = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Glide.iss"));
        var tasks = iss.Split("[Tasks]")[1].Split("[Files]")[0];
        Assert.Contains("Name: \"thumbnails\"; Description: \"Use Glide's thumbnails in File Explorer (recommended)\"", tasks);
        var normalized = iss.Replace("\r", "");
        Assert.Contains("if WizardIsTaskSelected('thumbnails') then\n      RegisterGlideThumbnails\n    else\n      UnregisterGlideThumbnails;", normalized);
        Assert.Contains("RefreshGlideThumbnailsForUser(WizardIsTaskSelected('thumbnails'));", iss);
        Assert.Contains("ExecAsOriginalUser(ExePath, Params", iss);
        Assert.Contains("'--refresh-thumbnails'", iss);
        Assert.Contains("' --disabled'", iss);
    }

    [Theory]
    [InlineData(new[] { "--refresh-thumbnails" }, true)]
    [InlineData(new[] { "--REFRESH-THUMBNAILS" }, true)]
    [InlineData(new[] { "--refresh-thumbnails", "--disabled" }, false)]
    public void Refresh_thumbnails_switch_parses_enabled_state(string[] args, bool expected)
        => Assert.Equal(expected, ThumbnailCacheRefresher.ParseRefreshSwitch(args));

    [Fact]
    public void Refresh_thumbnails_switch_is_absent_for_other_arguments()
    {
        Assert.Null(ThumbnailCacheRefresher.ParseRefreshSwitch([]));
        Assert.Null(ThumbnailCacheRefresher.ParseRefreshSwitch(["--register-thumbnails"]));
    }

    // ------------------------------------------------------------ thumbnail handler policy ---------

    private const string PhotoProvider = WindowsThumbnailRegistration.WindowsPhotoThumbnailProvider;
    private const string ThirdParty = "{11111111-2222-3333-4444-555555555555}";

    private static IReadOnlyList<string> Recommended(Dictionary<string, string> handlers, IEnumerable<string> codecFormats)
        => WindowsThumbnailRegistration.ResolveExtensions(ThumbnailProviderMode.Recommended, ImageFormatRegistry.Extensions,
            extension => handlers.TryGetValue(extension, out var clsid) ? clsid : null, codecFormats);

    [Fact]
    public void Recommended_takes_codec_formats_over_from_the_codec_dependent_photo_thumbnail_provider()
    {
        var handlers = new Dictionary<string, string>
        {
            [".heic"] = PhotoProvider, [".avif"] = PhotoProvider, [".cr2"] = PhotoProvider, [".jpg"] = PhotoProvider,
        };
        var codec = BundledCodecLibraries.BundledExtensions.Concat(BundledCodecLibraries.ManagedExtensions).ToArray();
        var set = Recommended(handlers, codec);
        Assert.Contains(".heic", set);   // blank page icon on a clean PC before
        Assert.Contains(".avif", set);
        Assert.Contains(".cr2", set);
        Assert.DoesNotContain(".jpg", set); // in-box WIC JPEG works everywhere: left to Windows
    }

    [Fact]
    public void Recommended_never_overrides_a_third_party_handler_for_codec_formats()
    {
        var handlers = new Dictionary<string, string> { [".heic"] = ThirdParty, [".pdn"] = ThirdParty, [".cr2"] = ThirdParty };
        var set = Recommended(handlers, BundledCodecLibraries.BundledExtensions.Concat(BundledCodecLibraries.ManagedExtensions));
        Assert.DoesNotContain(".heic", set);
        Assert.DoesNotContain(".pdn", set);
        Assert.DoesNotContain(".cr2", set);
    }

    [Fact]
    public void Without_the_codec_library_its_formats_are_not_taken_from_windows()
    {
        var handlers = new Dictionary<string, string> { [".heic"] = PhotoProvider };
        Assert.DoesNotContain(".heic", Recommended(handlers, Array.Empty<string>()));
        Assert.Contains(".heic", Recommended(new Dictionary<string, string>(), Array.Empty<string>())); // gap filling is unchanged
    }

    [Fact]
    public void Unsupported_only_and_all_modes_are_unchanged()
    {
        var handlers = new Dictionary<string, string> { [".heic"] = PhotoProvider, [".webp"] = PhotoProvider };
        var unsupported = WindowsThumbnailRegistration.ResolveExtensions(ThumbnailProviderMode.UnsupportedOnly, ImageFormatRegistry.Extensions,
            extension => handlers.TryGetValue(extension, out var clsid) ? clsid : null, BundledCodecLibraries.BundledExtensions);
        Assert.DoesNotContain(".heic", unsupported);
        Assert.DoesNotContain(".webp", unsupported);
        var all = WindowsThumbnailRegistration.ResolveExtensions(ThumbnailProviderMode.All, ImageFormatRegistry.Extensions, _ => ThirdParty, Array.Empty<string>());
        Assert.Equal(ImageFormatRegistry.Extensions.Count, all.Count);
    }

    [Fact]
    public void Taking_over_a_handler_remembers_it_for_unregister_but_never_remembers_glide()
    {
        Assert.Equal(PhotoProvider, WindowsThumbnailRegistration.PreviousHandlerToRemember(PhotoProvider));
        Assert.Null(WindowsThumbnailRegistration.PreviousHandlerToRemember(WindowsThumbnailRegistration.ProviderClsid)); // re-register keeps the original
        Assert.Null(WindowsThumbnailRegistration.PreviousHandlerToRemember(null));
        Assert.Null(WindowsThumbnailRegistration.PreviousHandlerToRemember("not a clsid"));
    }

    [Fact]
    public void Codec_library_manifest_extensions_are_read_and_a_missing_library_claims_nothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "glide-codecs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Empty(BundledCodecLibraries.AvailableExtensions(directory));
            File.WriteAllBytes(Path.Combine(directory, "glidecodecs.dll"), new byte[] { 1 });
            Assert.Equal(BundledCodecLibraries.BundledExtensions, BundledCodecLibraries.AvailableExtensions(directory));
            // A -NoHevc build lists no HEIF suffixes in its manifest.
            File.WriteAllText(Path.Combine(directory, "glidecodecs.glidecodec.json"), "{\"extensions\": [\".avif\", \".JXL\"]}");
            Assert.Equal(new[] { ".avif", ".jxl" }, BundledCodecLibraries.AvailableExtensions(directory));
            File.WriteAllBytes(Path.Combine(directory, "glidemanaged.dll"), new byte[] { 1 });
            Assert.Contains(".exr", BundledCodecLibraries.AvailableExtensions(directory));
        }
        finally { TempDir.TryDelete(directory); }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Glide.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
