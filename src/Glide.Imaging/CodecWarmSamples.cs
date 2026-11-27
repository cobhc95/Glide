namespace Glide.Imaging;

/// <summary>
/// Four tiny pictures (32 x 16, 46 to 1601 bytes: the test fixtures of the bundled codec plug-in) that <see cref="CodecPreloader"/>
/// decodes once, after loading the plug-in, to initialise its decoders (thread pools, tables, the managed hand-over path) before
/// the first real file is opened. Each is written to a temporary file for the plug-in (its API takes a path) and deleted at once.
/// </summary>
internal static class CodecWarmSamples
{
    /// <summary>The sample for a suffix served by the plug-in, or null.</summary>
    public static byte[]? For(string extension, out string sampleExtension)
    {
        switch (extension)
        {
            case ".heic" or ".heif" or ".heics" or ".heifs" or ".hif":
                sampleExtension = ".heic";
                return Convert.FromBase64String(Heic);
            case ".avif" or ".avifs":
                sampleExtension = ".avif";
                return Convert.FromBase64String(Avif);
            case ".jxl":
                sampleExtension = ".jxl";
                return Convert.FromBase64String(Jxl);
            case ".jp2" or ".j2k" or ".j2c" or ".jpc" or ".jpx" or ".jpf" or ".jpm" or ".mj2":
                sampleExtension = ".jp2";
                return Convert.FromBase64String(Jp2);
            default:
                sampleExtension = "";
                return null;
        }
    }

    private const string Avif =
        "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUEAAADrbWV0YQAAAAAAAAAhaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAA" +
        "AAAAAAAOcGl0bQAAAAAAAQAAAB5pbG9jAAAAAEQAAAEAAQAAAAEAAAETAAAAKwAAAChpaW5mAAAAAAABAAAAGmluZmUCAAAAAAEA" +
        "AGF2MDFDb2xvcgAAAABqaXBycAAAAEtpcGNvAAAAFGlzcGUAAAAAAAAAIAAAABAAAAAQcGl4aQAAAAADCAgIAAAADGF2MUOBIAAA" +
        "AAAAE2NvbHJuY2x4AAEADQAGgAAAABdpcG1hAAAAAAAAAAEAAQQBAoMEAAAAM21kYXQSAAoIOBD/7CAhoNIyHUQAAOeIPlC5VrQA" +
        "8A2Cvohb61ptWGQpC0YX3wFA";

    private const string Heic =
        "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" +
        "JmqIkzMRERCIZERM27jDszbuMPu6xIuL9fA=";

    private const string Jxl =
        "/wrDQSQIAAEAjABLGJOOg4MpUCCQl9thVRcIAoCUFxDA7/3e7/1Zep8cf+wHAQ==";

    private const string Jp2 =
        "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAEAAAACAAAwcHAAAAAAAPY29scgEAAAAA" +
        "ABAAAAGzanAyY/9P/1EALwAAAAAAIAAAABAAAAAAAAAAAAAAACAAAAAQAAAAAAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAQEEBAAB" +
        "/1wAB0BASEhQ/2QAJQABQ3JlYXRlZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAABOAAB/5PPtPgRUFMZCxAAZsU8" +
        "6KQAAMI8oaa/MUCMNuT0w8oEa+hrjT5cegOI+fByIj7JUv8hRhIDQ6qI+tQ6VRRhHu5nv/8aYEODgIlCUODhVwZXElYOAh4zIrLo" +
        "a3h1H1W7XoakYz0AKS4cUdtQA8MwCHjx5l4EgA52KAQC4q9IJIu+A+nYI1hJF3wH6HCdYSRd8B//GfgDgqRQRYWQFx1AqLJyxikn" +
        "traLB/OEA4do1KOWUEyAADIRL07EbDf0MANMB9RnDjAYK9vElCgAYKdwElCgAYPH2hMfaEwfUCCCsdqIJsGhkndDg51/uWpmLYE4" +
        "r9lfz8AWPtBQNp4/s38A1OG0b9+YVPwE4/AEOKsTsfCjqWaP5QODoItjC3aZkclNqv4kBFzoe5w4r9lz/9k=";
}
