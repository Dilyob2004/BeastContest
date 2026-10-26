using System.Text.Json;
using BeastContest.Web.Models;
using Microsoft.JSInterop;

namespace BeastContest.Web.Services;

public sealed class Translations
{
    private readonly Dictionary<string, Dictionary<string, string>> _values;
    public Translations(IWebHostEnvironment environment)
    {
        _values = new[] { "ru", "en" }.ToDictionary(language => language,
            language => JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(Path.Combine(environment.ContentRootPath, "Resources", language + ".json")))!);
        if (!_values["ru"].Keys.Order().SequenceEqual(_values["en"].Keys.Order()))
            throw new InvalidOperationException("Translation resources must have matching keys.");
    }
    public string Get(string language, string key) => _values[language].GetValueOrDefault(key, key);
}

public sealed class UiState(Translations translations, IJSRuntime js)
{
    public string Language { get; private set; } = "ru";
    public string Theme { get; private set; } = "dark";
    public event Action? Changed;
    public string this[string key] => translations.Get(Language, key);
    public string Text(Translated text) => Language == "ru"
        ? (string.IsNullOrWhiteSpace(text.Ru) ? text.En : text.Ru)
        : (string.IsNullOrWhiteSpace(text.En) ? text.Ru : text.En);
    public string Scoring(ScoringMode mode) => this[mode == ScoringMode.Icpc ? "scoring.icpc" : "scoring.points"];
    public async Task InitializeAsync()
    {
        var preferences = await js.InvokeAsync<string[]>("beast.preferences");
        Language = preferences[0] == "en" ? "en" : "ru";
        Theme = preferences[1] == "light" ? "light" : "dark";
        await ApplyAsync();
    }
    public async Task ToggleLanguageAsync() { Language = Language == "ru" ? "en" : "ru"; await ApplyAsync(); }
    public async Task ToggleThemeAsync() { Theme = Theme == "dark" ? "light" : "dark"; await ApplyAsync(); }
    private async Task ApplyAsync()
    {
        await js.InvokeVoidAsync("beast.apply", Language, Theme);
        Changed?.Invoke();
    }
}
