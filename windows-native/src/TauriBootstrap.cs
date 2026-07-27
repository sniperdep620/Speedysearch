using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Speedysearch.Windows
{
    /// <summary>
    /// Compatibility entry point for people who previously launched the
    /// Windows-native build output directly. It never creates the legacy
    /// WinForms launcher; it forwards to the shared Tauri/React application.
    /// </summary>
    internal static class TauriBootstrap
    {
        [STAThread]
        private static int Main(string[] args)
        {
            string executable = FindTauriExecutable();
            if (executable == null)
            {
                MessageBox.Show(
                    "The Speedysearch Tauri launcher has not been built yet.\r\n\r\n"
                    + "From the repository root, run:\r\n"
                    + "npm install\r\n"
                    + "npm run tauri build -- --no-bundle",
                    "Speedysearch",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return 1;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = String.Join(" ", args.Select(QuoteArgument).ToArray()),
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false
            });
            return 0;
        }

        private static string FindTauriExecutable()
        {
            List<string> candidates = new List<string>();
            string configured = Environment.GetEnvironmentVariable("SPEEDYSEARCH_TAURI_PATH");
            if (!String.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }

            string directory = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(Path.Combine(directory, "speedysearch-ui.exe"));
            candidates.Add(Path.GetFullPath(Path.Combine(
                directory,
                "..",
                "..",
                "..",
                "src-tauri",
                "target",
                "release",
                "speedysearch-ui.exe")));

            return candidates.FirstOrDefault(delegate(string candidate)
            {
                return File.Exists(candidate);
            });
        }

        private static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return "\"\"";
            }
            if (value.All(delegate(char character)
            {
                return !Char.IsWhiteSpace(character) && character != '"';
            }))
            {
                return value;
            }
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
