using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace GmapPlanner.App.Localization;

/// <summary>
/// English/Hebrew UI strings. XAML uses <c>{l:T Key}</c>; code uses <see cref="T"/> / <see cref="F"/>.
/// Bindings go through an IObservable (<c>ToBinding</c>), not a reflection binding, so switching
/// language re-renders every string without tripping trimming rule #2 in CLAUDE.md.
/// </summary>
public static class Loc
{
    public static bool IsHebrew { get; private set; }
    public static event Action? Changed;

    public static void SetHebrew(bool hebrew)
    {
        if (IsHebrew == hebrew) return;
        IsHebrew = hebrew;
        Changed?.Invoke();
    }

    /// <summary>The string for <paramref name="key"/> in the current language; the key itself when unknown.</summary>
    public static string T(string key) =>
        Strings.Table.TryGetValue(key, out var s) ? (IsHebrew ? s.He : s.En) : key;

    public static string F(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, T(key), args);

    /// <summary>Progress lines come from Core in English; map them (including the numbered ones).</summary>
    public static string Progress(string step)
    {
        var m = Regex.Match(step, @"^Creating map (\d+)/(\d+): (.*)$");
        if (m.Success) return F("ProgCreatingMap", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        m = Regex.Match(step, @"^(Sharing map|Created map|Map) (\d+)/(\d+)( failed)?$");
        if (m.Success)
        {
            var key = m.Groups[1].Value switch { "Sharing map" => "ProgSharingMap", "Created map" => "ProgCreatedMap", _ => "ProgMapFailed" };
            return F(key, m.Groups[2].Value, m.Groups[3].Value);
        }
        return T(step);
    }

    internal static IObservable<string> Observe(string key) => new KeyObservable(key);

    private sealed class KeyObservable(string key) : IObservable<string>
    {
        public IDisposable Subscribe(IObserver<string> observer)
        {
            void Push() => observer.OnNext(T(key));
            Push();
            Changed += Push;
            return new Unsubscribe(() => Changed -= Push);
        }
    }

    private sealed class Unsubscribe(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

/// <summary><c>Text="{l:T MakeAMap}"</c> — a live, language-aware string.</summary>
public sealed class T(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Observe(key).ToBinding();
}
