namespace GmapPlanner.Core.Prompt;

public static class TripNamePrompt
{
    public static string For(string fileName) => $$"""
        Suggest a short name for the trip in the attached travel itinerary.

        The uploaded file is named: "{{fileName}}"

        - If the file name is meaningful (it names a destination, travelers or the trip itself, e.g.
          "Japan 2025 - Dana & Tom.pdf" or "טיול משפחתי ליוון.pdf"), base the name on it.
        - If the file name is generic or random (e.g. "document(2).pdf", "IMG_4821.pdf", "itinerary.txt",
          "Untitled.pdf", a hash or a date only), ignore it and base the name on the document body
          (destination, travelers, month/year).
        - The name MUST be in Hebrew. Keep it short: 2-6 words. No emojis, no quotes, no day numbers.

        Return ONLY a JSON object: {"trip_name": "<name in Hebrew>"}
        """;
}
