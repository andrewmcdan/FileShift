namespace FileShift.Core;

public record ConversionRoute(string Target, string Engine, string Description);

public static class Formats
{
    public const string Native = "Windows images", Media = "FFmpeg", Images = "ImageMagick", Office = "LibreOffice", Text = "Pandoc", Archive = "Built-in ZIP", Adobe = "Adobe Media Encoder", Ghostscript = "Ghostscript", Ebook = "Calibre";
    private static readonly HashSet<string> Audio = Set("wav mp3 flac aac m4a ogg opus wma aiff aif alac ape amr ac3 au mka");
    private static readonly HashSet<string> Video = Set("mp4 mkv mov avi webm wmv m4v mpg mpeg ts mts m2ts flv vob 3gp ogv");
    private static readonly HashSet<string> Bitmap = Set("png jpg jpeg bmp gif tif tiff ico");
    private static readonly HashSet<string> Image = Set("png jpg jpeg jpe bmp dib gif tif tiff ico webp avif heic heif svg svgz eps ps ai psd psb tga dds exr hdr dng cr2 cr3 nef nrw arw raf orf rw2 pef srw jp2 j2k jxl ppm pgm pbm pnm pcx xbm xpm");
    private static readonly HashSet<string> Writer = Set("doc docx odt rtf txt html htm wps sxw fodt dot dotx");
    private static readonly HashSet<string> Sheet = Set("xls xlsx xlsm ods csv tsv xlsb fods");
    private static readonly HashSet<string> Slides = Set("ppt pptx pptm odp pps ppsx fodp");
    private static readonly HashSet<string> Drawing = Set("odg fodg vsd vsdx pub");
    private static readonly HashSet<string> Ebooks = Set("epub mobi azw azw3 fb2 lit lrf pdb prc rb snb tcr chm djvu docx odt rtf txt html htm pdf cbz cbr");
    private static readonly Dictionary<string, string> PandocReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md"]="markdown", ["markdown"]="markdown", ["html"]="html", ["htm"]="html", ["docx"]="docx",
        ["odt"]="odt", ["epub"]="epub", ["rst"]="rst", ["tex"]="latex", ["org"]="org", ["ipynb"]="ipynb"
    };
    private static readonly Dictionary<string, string> PandocWriters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md"]="markdown", ["html"]="html5", ["docx"]="docx", ["odt"]="odt", ["epub"]="epub3",
        ["rst"]="rst", ["tex"]="latex", ["org"]="org", ["txt"]="plain"
    };
    public static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    public static bool IsVideo(string path) => Video.Contains(Extension(path));
    public static bool IsAudioTarget(string target) => Set("mp3 wav flac m4a ogg opus aac aiff").Contains(target);
    public static string Reader(string path) => PandocReaders[Extension(path)];
    public static string WriterFormat(string target) => PandocWriters[target];
    public static IReadOnlyList<string> SourceExtensions => Audio.Concat(Video).Concat(Bitmap).Concat(Image).Concat(Writer)
        .Concat(Sheet).Concat(Slides).Concat(Drawing).Concat(Ebooks).Concat(PandocReaders.Keys).Append("prproj")
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public static IReadOnlyList<ConversionRoute> Routes(string path)
    {
        var ext = Extension(path);
        var routes = new List<ConversionRoute>();
        void Add(string targets, string engine, string description)
        {
            routes.AddRange(targets.Split(' ').Where(t => t != ext).Select(t => new ConversionRoute(t, engine, description)));
        }
        if (Audio.Contains(ext) || Video.Contains(ext)) Add("mp3 wav flac m4a ogg opus aac aiff", Media, "Audio / extract audio");
        if (Video.Contains(ext)) Add("mp4 mkv webm mov avi gif", Media, "Video / animation");
        if (Bitmap.Contains(ext)) Add("png jpg bmp gif tif", Native, "Built-in image conversion (first frame)");
        if (Image.Contains(ext)) Add("png jpg webp avif heic bmp gif tif ico pdf", ext is "ai" or "eps" or "ps" ? Images + "+" + Ghostscript : Images, "Flattened image (first frame/page/artboard)");
        if (ext == "pdf") Add("png jpg tif", Images + "+" + Ghostscript, "First PDF page");
        if (Writer.Contains(ext)) Add("pdf docx odt rtf txt", Office, "Document conversion");
        if (Sheet.Contains(ext)) Add("pdf xlsx ods csv", Office, "Spreadsheet (CSV exports one sheet)");
        if (Slides.Contains(ext)) Add("pdf pptx odp", Office, "Presentation conversion");
        if (Drawing.Contains(ext)) Add("pdf", Office, "Drawing / publishing document");
        if (PandocReaders.ContainsKey(ext)) Add("md html docx odt epub rst tex org txt", Text, "Document structure / ebook");
        // PDF creation for markup uses Pandoc -> DOCX -> LibreOffice, avoiding a separate TeX install.
        if (PandocReaders.ContainsKey(ext) && !routes.Any(r => r.Target == "pdf"))
            routes.Add(new("pdf", Text + "+" + Office, "Document to PDF"));
        if (Ebooks.Contains(ext))
            Add("epub mobi azw3 fb2 docx pdf txt rtf", Ebook, "Ebook / document reflow (no DRM or OCR; PDF layout may change)");
        if (ext == "prproj") Add("mp4 mov mp3 wav", Adobe, "Premiere render · requires matching .epr preset and a single root sequence");
        routes.Add(new("zip", Archive, "Create one ZIP archive containing all selected items"));
        return routes;
    }
    public static IReadOnlyList<ConversionRoute> AvailableRoutes(string path, EnginePaths engines) =>
        Routes(path).GroupBy(r => r.Target).Select(g => g.FirstOrDefault(r => engines.Available(r.Engine)) ?? g.First()).OrderBy(r => r.Target).ToArray();
    public static ConversionRoute Resolve(string source, string target, EnginePaths engines) =>
        Routes(source).FirstOrDefault(r => r.Target == target && engines.Available(r.Engine))
        ?? throw new InvalidOperationException($"No available engine for {Extension(source).ToUpperInvariant()} → {target.ToUpperInvariant()}. Check Engines & setup.");
    private static HashSet<string> Set(string text) => new(text.Split(' '), StringComparer.OrdinalIgnoreCase);
}
