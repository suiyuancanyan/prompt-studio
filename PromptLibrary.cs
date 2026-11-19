using System.IO;
using System.Reflection;
using System.Text.Json;

namespace PromptStudio;

internal sealed record PromptTemplate(string Title, string Prompt, string[] Tags, string? Source)
{
    public string Categories => string.Join(" · ", Tags);
}

internal static class PromptLibrary
{
    public const string SourceUrl = "https://github.com/rockbenben/ChatGPT-Shortcut";

    private static readonly Lazy<PromptTemplate[]> Templates = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PromptStudio.模板.AiShort.json")
            ?? throw new InvalidOperationException("找不到内置提示词库。");
        return JsonSerializer.Deserialize<PromptTemplate[]>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? [];
    });

    public static PromptTemplate[] Search(string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return Templates.Value;

        return Templates.Value
            .Where(template => words.All(word =>
                template.Title.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                template.Tags.Any(tag => tag.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
                template.Prompt.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(template => words.All(word => template.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
