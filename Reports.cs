using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CrossCrease
{
    // Where a game project leaves its crash file and logs ("crash_reports" in a profile).
    // The crash file is a few key=value lines the game runtime writes when it crashes.
    public sealed class CrashReportOptions
    {
        public string crash_file { get; set; }
        public string[] log_files { get; set; }
    }

    // A crash or problem report as the CrossCrease website receives it (see the website's reports.py).
    // Nothing is sent unless the player chooses Send; the dialog shows the whole report first.
    public sealed partial class MainForm
    {
        const int MaxLogChars = 256 * 1024;
        const int MaxCrashChars = 16 * 1024;
        bool reportDialogOpen;

        static string ReportUrl
        {
            get
            {
                // CROSSCREASE_REPORT_URL is for automated tests and local website development only.
                string custom = Environment.GetEnvironmentVariable("CROSSCREASE_REPORT_URL");
                return string.IsNullOrWhiteSpace(custom) ? BuildInfo.ReportUrl : custom.Trim();
            }
        }

        string CrashFile(LibraryItem item)
        {
            var options = item.Profile.crash_reports;
            return options == null ? "" : ResolvePath(item.Profile, options.crash_file);
        }

        // Offers to send a report for each game that left a crash file (only that game when one is given).
        // The crash file is renamed afterwards whatever the player chose, so each crash is offered once.
        void OfferCrashReports(LibraryItem only)
        {
            if (reportDialogOpen) return;
            foreach (var item in items.ToArray())
            {
                if (only != null && item != only) continue;
                string file = CrashFile(item);
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                string crash;
                DateTime when;
                try
                {
                    crash = ReadTail(file, MaxCrashChars);
                    when = File.GetLastWriteTimeUtc(file);
                    File.Copy(file, Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".previous" + Path.GetExtension(file)), true);
                    File.Delete(file);
                }
                catch { continue; }
                ShowReportDialog(item, crash, when);
            }
        }

        void ShowReportDialog(LibraryItem item, string crash, DateTime? when)
        {
            if (reportDialogOpen || item == null) return;
            reportDialogOpen = true;
            try
            {
                string onlineId = FindSetting(item.Profile, "online_id") != null ? SettingValue(item.Profile, "online_id") : null;
                using (var dialog = new ReportDialog(this, item, crash, when, onlineId))
                    dialog.ShowDialog(DialogOwner);
            }
            finally { reportDialogOpen = false; }
        }

        internal Dictionary<string, object> BuildReport(LibraryItem item, string crash, DateTime? when, string comment, bool includeLog, string onlineId, bool includeOnlineId)
        {
            var profile = item.Profile;
            var packageVersions = new Dictionary<string, object>();
            if (profile.packages != null)
                foreach (var id in profile.packages)
                {
                    string version = packages.InstalledVersion(profile.id, id);
                    if (!string.IsNullOrWhiteSpace(version)) packageVersions[id] = version;
                }
            var log = new StringBuilder();
            if (includeLog && profile.crash_reports != null && profile.crash_reports.log_files != null)
            {
                int budget = MaxLogChars / Math.Max(1, profile.crash_reports.log_files.Length);
                foreach (var value in profile.crash_reports.log_files)
                {
                    string file = ResolvePath(profile, value);
                    if (string.IsNullOrEmpty(file) || !File.Exists(file)) continue;
                    try { log.Append("=== " + Path.GetFileName(file) + " ===\n").Append(ReadTail(file, budget)).Append('\n'); } catch { }
                }
            }
            var secrets = new List<string>();
            var saved = LoadSettings(profile);
            if (profile.settings != null)
                foreach (var setting in profile.settings)
                {
                    string value;
                    if (setting.secret && saved.TryGetValue(setting.id, out value) && !string.IsNullOrEmpty(value)) secrets.Add(value);
                }
            string hiddenId = includeOnlineId ? null : onlineId;
            var report = new Dictionary<string, object> {
                { "format", "crosscrease-report" },
                { "version", 1 },
                { "game", profile.id },
                { "kind", crash != null ? "crash" : "problem" },
                { "launcherVersion", BuildInfo.Version },
                { "packages", packageVersions },
                { "system", SystemDescription() },
                { "occurredAt", when.HasValue ? when.Value.ToString("yyyy-MM-ddTHH:mm:ssZ") : "" },
                { "onlineId", includeOnlineId && !string.IsNullOrEmpty(onlineId) ? onlineId : "" },
                { "comment", Scrub((comment ?? "").Trim(), secrets, hiddenId) },
                { "crash", Scrub(crash ?? "", secrets, hiddenId) },
                { "log", Scrub(log.ToString(), secrets, hiddenId) }
            };
            return report;
        }

        static string ReadTail(string file, int maxChars)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - maxChars);
                stream.Seek(start, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd().Replace("\r\n", "\n");
                    if (start > 0)
                    {
                        int newline = text.IndexOf('\n');
                        if (newline >= 0) text = text.Substring(newline + 1);
                    }
                    return text.Length > maxChars ? text.Substring(text.Length - maxChars) : text;
                }
            }
        }

        static readonly Regex SecretPattern = new Regex(
            "\\b(password|passwd|pwd|passphrase|token|ticket|secret|authorization|cookie|session[_-]?key|api[_-]?key|auth[_-]?code)(\"?\\s*[:=]\\s*\"?)([^\\s\"',;&}]+)",
            RegexOptions.IgnoreCase);
        static readonly Regex WindowsUserPattern = new Regex("\\b([A-Z]:[\\\\/]+(?:Users|Documents and Settings)[\\\\/]+)([^\\\\/\\r\\n\"'<>|]+)", RegexOptions.IgnoreCase);
        static readonly Regex EmailPattern = new Regex("[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\\.[A-Za-z0-9-]+)*\\.[A-Za-z]{2,}");

        // Removes passwords, tokens, e-mail addresses, the Windows user name and (unless the player
        // shares it) the online ID. The website repeats the general rules for other clients.
        static string Scrub(string text, List<string> secrets, string hiddenOnlineId)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (var secret in secrets) if (secret.Length >= 4) text = text.Replace(secret, "<removed>");
            text = SecretPattern.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "<removed>");
            text = WindowsUserPattern.Replace(text, m => m.Groups[1].Value + "<user>");
            string user = Environment.UserName;
            if (!string.IsNullOrEmpty(user) && user.Length >= 3)
                text = Regex.Replace(text, "\\b" + Regex.Escape(user) + "\\b", "<user>", RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(hiddenOnlineId) && hiddenOnlineId.Length >= 3)
                text = Regex.Replace(text, "\\b" + Regex.Escape(hiddenOnlineId) + "\\b", "<online id>", RegexOptions.IgnoreCase);
            return EmailPattern.Replace(text, "<e-mail>");
        }

        static string SystemDescription()
        {
            string windows = "Windows";
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (key != null)
                        windows = string.Format("Windows {0}.{1}.{2}.{3}", key.GetValue("CurrentMajorVersionNumber", 10), key.GetValue("CurrentMinorVersionNumber", 0),
                            key.GetValue("CurrentBuildNumber", "0"), key.GetValue("UBR", 0));
            }
            catch { }
            string text = windows + (Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit");
            var gpus = new List<string>();
            try
            {
                using (var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                    if (display != null)
                        foreach (var name in display.GetSubKeyNames())
                        {
                            if (!Regex.IsMatch(name, "^\\d{4}$")) continue;
                            try
                            {
                                using (var adapter = display.OpenSubKey(name))
                                {
                                    string description = adapter == null ? null : adapter.GetValue("DriverDesc") as string;
                                    string version = adapter == null ? null : adapter.GetValue("DriverVersion") as string;
                                    if (!string.IsNullOrWhiteSpace(description) && !gpus.Contains(description.Trim() + (version != null ? " (driver " + version + ")" : "")))
                                        gpus.Add(description.Trim() + (version != null ? " (driver " + version + ")" : ""));
                                }
                            }
                            catch { }
                        }
            }
            catch { }
            if (gpus.Count > 0) text += "; " + string.Join(", ", gpus.ToArray());
            return text.Length > 200 ? text.Substring(0, 200) : text;
        }

        // Sends the report. Returns null on success, otherwise a message for the player.
        internal static string SendReport(Dictionary<string, object> report, out int reportId)
        {
            reportId = 0;
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                byte[] body = Encoding.UTF8.GetBytes(serializer.Serialize(report));
                var request = (HttpWebRequest)WebRequest.Create(ReportUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.UserAgent = PackageStore.UserAgent;
                request.Headers["X-CrossCrease-Client"] = "launcher/" + BuildInfo.Version;
                request.Timeout = request.ReadWriteTimeout = 30000;
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    var result = serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                    object id;
                    if (result != null && result.TryGetValue("id", out id) && id is int) reportId = (int)id;
                }
                return null;
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) return "CrossCrease could not reach crosscrease.com. Check your internet connection and try again.";
                try
                {
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                        object error;
                        if (result != null && result.TryGetValue("error", out error) && error is string) return "The report was not accepted: " + error;
                    }
                }
                catch { }
                return "The report was not accepted (" + (int)response.StatusCode + ").";
            }
            catch (Exception ex) { return "The report could not be sent: " + ex.Message; }
        }
    }

    sealed class ReportDialog : Form
    {
        readonly MainForm owner;
        readonly LibraryItem item;
        readonly string crash;
        readonly DateTime? when;
        readonly string onlineId;
        readonly bool offerId;
        readonly TextBox comment;
        readonly CheckBox includeLog, includeId;
        readonly Label status;
        readonly FlatButton send, skip;

        public ReportDialog(MainForm owner, LibraryItem item, string crash, DateTime? when, string onlineId)
        {
            this.owner = owner;
            this.item = item;
            this.crash = crash;
            this.when = when;
            this.onlineId = onlineId;
            offerId = !string.IsNullOrEmpty(onlineId);
            bool isCrash = crash != null;
            Text = isCrash ? item.Profile.name + " closed unexpectedly" : "Report a problem with " + item.Profile.name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            if (Ui.AppIcon != null) Icon = Ui.AppIcon;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(24, 27, 33);
            ForeColor = Ui.Text;
            Font = new Font("Segoe UI", 10f);
            int pad = Ui.S(24), width = Ui.S(480), y = pad;
            Controls.Add(new Label { Text = Text, Font = Ui.HeavyFont(14f), AutoSize = true, Location = new Point(pad - Ui.S(2), y) });
            y += Ui.S(42);
            y = AddText(pad, y, width, isCrash
                ? "Send a crash report to the CrossCrease team? It helps us find and fix the crash. Nothing is sent unless you choose Send."
                : "Tell the CrossCrease team what went wrong. The more you describe, the easier it is to fix.", Ui.Text);
            Controls.Add(new Label { Text = isCrash ? "What were you doing? (optional)" : "What went wrong?", Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold), AutoSize = true, Location = new Point(pad, y) });
            y += Ui.S(26);
            comment = new TextBox {
                Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true, MaxLength = 4000,
                Location = new Point(pad, y), Size = new Size(width, Ui.S(96)),
                BackColor = Color.FromArgb(38, 42, 50), ForeColor = Ui.Text, BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = isCrash ? "What were you doing" : "What went wrong"
            };
            Controls.Add(comment);
            y += comment.Height + Ui.S(14);
            bool hasLog = item.Profile.crash_reports != null && item.Profile.crash_reports.log_files != null && item.Profile.crash_reports.log_files.Length > 0;
            includeLog = new CheckBox { Text = "Include the end of the game log", Checked = hasLog, Enabled = hasLog, AutoSize = true, Location = new Point(pad, y), FlatStyle = FlatStyle.Flat };
            Controls.Add(includeLog);
            y += Ui.S(30);
            includeId = new CheckBox { Text = "Include my online ID" + (offerId ? " (" + onlineId + ")" : ""), Checked = false, AutoSize = true, Location = new Point(pad, y), FlatStyle = FlatStyle.Flat, Visible = offerId };
            Controls.Add(includeId);
            if (offerId) y += Ui.S(30);
            y += Ui.S(4);
            y = AddText(pad, y, width, (offerId ? "With your online ID the team can reply to you in the game. " : "") + "The report also contains " + (isCrash ? "the crash details, " : "") + "the launcher and runtime versions, your Windows version and graphics card. " +
                "Passwords, tokens, e-mail addresses and your Windows user name are removed. Only the CrossCrease team can read reports; they are deleted after 90 days.", Ui.Muted);
            var preview = new LinkLabel { Text = "Show what will be sent", AutoSize = true, Location = new Point(pad, y), LinkColor = item.Accent, ActiveLinkColor = Ui.Text };
            preview.LinkClicked += delegate { ShowPreview(); };
            Controls.Add(preview);
            y += Ui.S(32);
            status = new Label { ForeColor = Ui.Amber, Location = new Point(pad, y), MaximumSize = new Size(width, 0), AutoSize = true };
            Controls.Add(status);
            y += Ui.S(30);
            send = new FlatButton { Kind = ButtonKind.Primary, Accent = item.Accent, Text = "Send report" };
            skip = new FlatButton { Kind = ButtonKind.Secondary, Text = isCrash ? "Don't send" : "Cancel" };
            int h = Ui.S(38), sendWidth = Math.Max(Ui.S(130), send.PreferredWidth()), skipWidth = skip.PreferredWidth();
            send.SetBounds(pad + width - sendWidth, y, sendWidth, h);
            skip.SetBounds(send.Left - Ui.S(10) - skipWidth, y, skipWidth, h);
            send.Click += delegate { Send(); };
            skip.Click += delegate { DialogResult = DialogResult.Cancel; };
            Controls.Add(send);
            Controls.Add(skip);
            ClientSize = new Size(width + pad * 2, y + h + pad);
        }

        int AddText(int x, int y, int width, string text, Color color)
        {
            var label = new Label { Text = text, ForeColor = color, Location = new Point(x, y), MaximumSize = new Size(width, 0), AutoSize = true };
            Controls.Add(label);
            return y + label.PreferredHeight + Ui.S(16);
        }

        Dictionary<string, object> Report()
        {
            return owner.BuildReport(item, crash, when, comment.Text, includeLog.Checked, onlineId, offerId && includeId.Checked);
        }

        void ShowPreview()
        {
            var report = Report();
            var text = new StringBuilder();
            foreach (var pair in report)
            {
                if (pair.Key == "format" || pair.Key == "version") continue;
                var map = pair.Value as Dictionary<string, object>;
                string value = map != null ? string.Join(", ", new List<string>(Pairs(map)).ToArray()) : Convert.ToString(pair.Value);
                if (pair.Key == "crash" || pair.Key == "log" || pair.Key == "comment")
                    text.Append("--- ").Append(pair.Key).Append(" ---\r\n").Append(value.Replace("\n", "\r\n")).Append("\r\n\r\n");
                else
                    text.Append(pair.Key).Append(": ").Append(value).Append("\r\n");
            }
            using (var window = new Form {
                Text = "What will be sent", StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
                ClientSize = new Size(Ui.S(760), Ui.S(560)), BackColor = Color.FromArgb(24, 27, 33), Icon = Icon, MinimizeBox = false
            })
            {
                window.KeyPreview = true;
                window.KeyDown += (sender, e) => { if (e.KeyCode == Keys.Escape) window.Close(); };
                window.Controls.Add(new TextBox {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                    Font = new Font("Consolas", 9.5f), BackColor = Color.FromArgb(18, 20, 25), ForeColor = Ui.Text, BorderStyle = BorderStyle.None,
                    Text = text.ToString()
                });
                window.Shown += delegate { var box = (TextBox)window.Controls[0]; box.SelectionLength = 0; box.SelectionStart = 0; };
                window.ShowDialog(this);
            }
        }

        static IEnumerable<string> Pairs(Dictionary<string, object> map)
        {
            foreach (var pair in map) yield return pair.Key + " " + pair.Value;
        }

        void Send()
        {
            if (crash == null && comment.Text.Trim().Length == 0)
            {
                status.Text = "Please describe what went wrong.";
                comment.Focus();
                return;
            }
            var report = Report();
            send.Enabled = skip.Enabled = false;
            status.ForeColor = Ui.Muted;
            status.Text = "Sending …";
            var worker = new Thread(delegate()
            {
                int id;
                string error = MainForm.SendReport(report, out id);
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (error == null)
                        {
                            MessageBox.Show(this, "Thank you! Your report " + (id > 0 ? "#" + id + " " : "") + "was sent to the CrossCrease team.", "Report sent", MessageBoxButtons.OK, MessageBoxIcon.Information);
                            DialogResult = DialogResult.OK;
                            return;
                        }
                        status.ForeColor = Ui.Amber;
                        status.Text = error;
                        send.Enabled = skip.Enabled = true;
                    }));
                }
                catch (InvalidOperationException) { }
            }) { IsBackground = true };
            worker.Start();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape && skip.Enabled) { DialogResult = DialogResult.Cancel; return true; }
            return base.ProcessDialogKey(keyData);
        }
    }
}
