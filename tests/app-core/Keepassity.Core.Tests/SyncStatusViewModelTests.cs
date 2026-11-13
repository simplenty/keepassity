// SPDX-License-Identifier: GPL-3.0-or-later
// Headless behaviour checks for the sync status view model.
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

internal static class SyncStatusViewModelTests
{
    public static void Run(TestCheck check)
    {
        var text = new SwitchingText();
        var vm = new SyncStatusViewModel(text);
        check("sync status starts as local only", vm.Text == "Local only", vm.Text);

        vm.Set("Synced");
        check("sync status renders a plain status", vm.Text == "Synced", vm.Text);

        vm.Set("Retrying sync in {0} ms", 1500);
        check("sync status formats numeric arguments",
            vm.Text == "Retrying sync in 1500 ms", vm.Text);

        text.Language = "zh-CN";
        vm.Refresh();
        check("sync status refreshes after a language change",
            vm.Text == "[zh-CN] Retrying sync in 1500 ms", vm.Text);
        check("sync status keeps its source for diagnostics", vm.Source == "Retrying sync in {0} ms");
    }

    private sealed class SwitchingText : IAppText
    {
        public string Language { get; set; } = "en";

        public string T(string english)
            => Language == "en" ? english : $"[{Language}] {english}";

        public string T(string format, params object?[] arguments)
        {
            string text = arguments.Length == 0 ? format : string.Format(format, arguments);
            return Language == "en" ? text : $"[{Language}] {text}";
        }

        public string TLong(string key, string english)
            => Language == "en" ? english : $"[{Language}] {english}";
    }
}
