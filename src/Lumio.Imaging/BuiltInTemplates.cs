namespace Lumio.Imaging;

public static class BuiltInTemplates
{
    /// <summary>10x15 cm (1800x1200 px @ 300 DPI) Postkarte mit 1 großen Foto und elegantem Branding unten.</summary>
    public static BoothTemplate CreateSinglePhotoPostcard()
    {
        return new BoothTemplate
        {
            Id = "single-10x15",
            Name = "10x15 Postkarte (1 Einzelfoto)",
            Width = 1800,
            Height = 1200,
            BackgroundColorHex = "#12121E",
            Slots =
            [
                new TemplateSlot
                {
                    Index = 0,
                    X = 60,
                    Y = 60,
                    Width = 1680,
                    Height = 980,
                    CornerRadius = 18,
                    BorderWidth = 2,
                    BorderColorHex = "#33FFFFFF"
                }
            ],
            Texts =
            [
                new TemplateText
                {
                    Text = "{EventName}",
                    X = 900,
                    Y = 1110,
                    FontSize = 40,
                    ColorHex = "#FFFFFF",
                    Bold = true,
                    Alignment = "Center"
                },
                new TemplateText
                {
                    Text = "{Date}",
                    X = 900,
                    Y = 1155,
                    FontSize = 24,
                    ColorHex = "#A3A2BC",
                    Bold = false,
                    Alignment = "Center"
                }
            ]
        };
    }

    /// <summary>Upright 10x15 cm card (1200x1800 px) for portrait photos, e.g. from a phone or tablet held upright.</summary>
    public static BoothTemplate CreateSinglePhotoPortrait()
    {
        return new BoothTemplate
        {
            Id = "single-10x15-portrait",
            Name = "10x15 Hochformat (1 Einzelfoto)",
            Width = 1200,
            Height = 1800,
            BackgroundColorHex = "#12121E",
            Slots =
            [
                new TemplateSlot
                {
                    Index = 0,
                    X = 60,
                    Y = 60,
                    Width = 1080,
                    Height = 1560,
                    CornerRadius = 18,
                    BorderWidth = 2,
                    BorderColorHex = "#33FFFFFF"
                }
            ],
            Texts =
            [
                new TemplateText
                {
                    Text = "{EventName}",
                    X = 600,
                    Y = 1700,
                    FontSize = 40,
                    ColorHex = "#FFFFFF",
                    Bold = true,
                    Alignment = "Center"
                },
                new TemplateText
                {
                    Text = "{Date}",
                    X = 600,
                    Y = 1745,
                    FontSize = 24,
                    ColorHex = "#A3A2BC",
                    Bold = false,
                    Alignment = "Center"
                }
            ]
        };
    }

    /// <summary>
    /// The single photo postcard in the photo's orientation: a portrait photo gets the upright card instead of
    /// being cut down to a landscape strip. Other templates are returned unchanged.
    /// </summary>
    public static BoothTemplate MatchPhotoOrientation(BoothTemplate template, byte[] firstPhoto)
    {
        if (template.Id != "single-10x15")
            return template;

        try
        {
            using var codec = SkiaSharp.SKCodec.Create(new SkiaSharp.SKMemoryStream(firstPhoto));
            var info = codec?.Info;
            if (info is { } size && size.Height > size.Width)
                return CreateSinglePhotoPortrait();
        }
        catch
        {
            // Unreadable header: keep the chosen template
        }
        return template;
    }

    /// <summary>10x15 cm Postkarte mit 4 Fotos im 2x2 Raster.</summary>
    public static BoothTemplate CreateQuadCollage()
    {
        return new BoothTemplate
        {
            Id = "quad-10x15",
            Name = "10x15 Collage (4er Raster)",
            Width = 1800,
            Height = 1200,
            BackgroundColorHex = "#0F0F1A",
            Slots =
            [
                new TemplateSlot { Index = 0, X = 60, Y = 60, Width = 810, Height = 480, CornerRadius = 16 },
                new TemplateSlot { Index = 1, X = 930, Y = 60, Width = 810, Height = 480, CornerRadius = 16 },
                new TemplateSlot { Index = 2, X = 60, Y = 580, Width = 810, Height = 480, CornerRadius = 16 },
                new TemplateSlot { Index = 3, X = 930, Y = 580, Width = 810, Height = 480, CornerRadius = 16 }
            ],
            Texts =
            [
                new TemplateText
                {
                    Text = "✦ {EventName} ✦",
                    X = 900,
                    Y = 1140,
                    FontSize = 32,
                    ColorHex = "#FFB23D",
                    Bold = true,
                    Alignment = "Center"
                }
            ]
        };
    }

    /// <summary>
    /// Canon Selphy 10x15 Doppelstreifen: 2 identische 3er-Streifen nebeneinander,
    /// die man nach dem Druck in zwei 5x15 cm Streifen schneiden kann!
    /// </summary>
    public static BoothTemplate CreateDoubleStripForSelphy()
    {
        return new BoothTemplate
        {
            Id = "double-strip-10x15",
            Name = "Selphy Doppel-Fotostreifen (2x 3er Streifen)",
            Width = 1800,
            Height = 1200,
            BackgroundColorHex = "#FFFFFF",
            Slots =
            [
                // Linker Streifen (Fotos 0, 1, 2)
                new TemplateSlot { Index = 0, X = 50, Y = 50, Width = 780, Height = 310, CornerRadius = 8 },
                new TemplateSlot { Index = 1, X = 50, Y = 380, Width = 780, Height = 310, CornerRadius = 8 },
                new TemplateSlot { Index = 2, X = 50, Y = 710, Width = 780, Height = 310, CornerRadius = 8 },

                // Rechter Streifen (Fotos 0, 1, 2 dupliziert zum Teilen)
                new TemplateSlot { Index = 0, X = 970, Y = 50, Width = 780, Height = 310, CornerRadius = 8 },
                new TemplateSlot { Index = 1, X = 970, Y = 380, Width = 780, Height = 310, CornerRadius = 8 },
                new TemplateSlot { Index = 2, X = 970, Y = 710, Width = 780, Height = 310, CornerRadius = 8 }
            ],
            Texts =
            [
                new TemplateText { Text = "{EventName}", X = 440, Y = 1110, FontSize = 34, ColorHex = "#222222", Bold = true, Alignment = "Center" },
                new TemplateText { Text = "{Date}", X = 440, Y = 1150, FontSize = 22, ColorHex = "#666666", Bold = false, Alignment = "Center" },

                new TemplateText { Text = "{EventName}", X = 1360, Y = 1110, FontSize = 34, ColorHex = "#222222", Bold = true, Alignment = "Center" },
                new TemplateText { Text = "{Date}", X = 1360, Y = 1150, FontSize = 22, ColorHex = "#666666", Bold = false, Alignment = "Center" }
            ]
        };
    }

    public static IReadOnlyList<BoothTemplate> GetAll() =>
    [
        CreateSinglePhotoPostcard(),
        CreateQuadCollage(),
        CreateDoubleStripForSelphy()
    ];
}
