namespace Lumio.Imaging;

public sealed class TemplateSlot
{
    public int Index { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float CornerRadius { get; set; } = 0;
    public float RotationDegrees { get; set; } = 0;
    public float BorderWidth { get; set; } = 0;
    public string BorderColorHex { get; set; } = "#FFFFFF";
}

public sealed class TemplateText
{
    public string Text { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float FontSize { get; set; } = 32;
    public string ColorHex { get; set; } = "#FFFFFF";
    public string FontName { get; set; } = "Outfit";
    public bool Bold { get; set; } = true;
    public string Alignment { get; set; } = "Center"; // Left, Center, Right
}

public sealed class BoothTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Neues Template";

    /// <summary>Width in pixels for 300 DPI print output (e.g. 1800 for 15cm, 1200 for 10cm)</summary>
    public int Width { get; set; } = 1800;

    /// <summary>Height in pixels for 300 DPI print output (e.g. 1200 for 10cm)</summary>
    public int Height { get; set; } = 1200;

    public string BackgroundColorHex { get; set; } = "#0D0D14";

    /// <summary>Optional background image file (PNG/JPG)</summary>
    public string? BackgroundImagePath { get; set; }

    /// <summary>Optional PNG overlay with transparent cutouts and graphics</summary>
    public string? OverlayImagePath { get; set; }

    public List<TemplateSlot> Slots { get; set; } = [];

    public List<TemplateText> Texts { get; set; } = [];

    public int RequiredPhotosCount => Slots.Count == 0 ? 1 : Slots.Count;
}
