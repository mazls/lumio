namespace Lumio.Imaging;

public static class PhotoBrandingHelper
{
    public static string FormatEventText(string eventName, string format)
    {
        var year = DateTime.Now.Year.ToString();
        var date = DateTime.Now.ToString("dd.MM.yyyy");

        return format switch
        {
            "EventAndYear" => string.IsNullOrWhiteSpace(eventName) ? year : $"{eventName} – {year}",
            "EventAndDate" => string.IsNullOrWhiteSpace(eventName) ? date : $"{eventName} • {date}",
            "EventOnly" => eventName,
            "None" => string.Empty,
            _ => string.IsNullOrWhiteSpace(eventName) ? year : $"{eventName} – {year}"
        };
    }
}
