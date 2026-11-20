using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>Message box drawn in the app's theme (replacement for MessageBox.Show).</summary>
    public class FxMessage : Form
    {
        readonly FlatButton primary;

        FxMessage(string caption, string text, BannerKind kind, string okText, string cancelText, bool danger, string glyph)
        {
            Text = caption;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Font = Theme.Body;

            var root = new Stack { Pad = 20, Gap = 16, Dock = DockStyle.Fill, Bg = () => Theme.P.Back };
            root.Controls.Add(new Banner(kind, caption, text, glyph));

            var row = new Row { MinHeight = 38 };
            row.Add(new Panel(), -1);
            if (cancelText != null)
            {
                var cancel = new FlatButton(cancelText, BtnKind.Secondary);
                cancel.Click += (s, e) => DialogResult = DialogResult.Cancel;
                row.Add(cancel, Math.Max(110, (int)(cancel.PreferredWidth() / Theme.Scale)));
            }
            primary = new FlatButton(okText, danger ? BtnKind.Danger : BtnKind.Primary);
            primary.Click += (s, e) => DialogResult = DialogResult.OK;
            row.Add(primary, Math.Max(110, (int)(primary.PreferredWidth() / Theme.Scale)));
            root.Controls.Add(row);
            Controls.Add(root);

            int w = Theme.Dp(text != null && text.Length > 160 ? 560 : 460);
            ClientSize = new Size(w, root.HeightFor(w));
        }

        /// <summary>Diagnostics (--shot msg): builds a sample dialog without showing it modally.</summary>
        internal static Form Sample()
        {
            return new FxMessage(L.T("Безопасное извлечение"), L.F("«{0}» можно отключить от компьютера.", "ST500LM0 12 HN-M500MBB"), BannerKind.Success, "OK", null, false, null);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.Apply(this);
            Theme.ApplyTitleBar(this);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); primary.Focus(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (e.KeyCode == Keys.Enter) DialogResult = DialogResult.OK;
        }

        static BannerKind Kind(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Error: return BannerKind.Danger;
                case MessageBoxIcon.Warning: return BannerKind.Warning;
                case MessageBoxIcon.Information: return BannerKind.Success;
                default: return BannerKind.Info;
            }
        }

        static DialogResult Run(IWin32Window owner, FxMessage f)
        {
            using (f)
            {
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                return owner != null ? f.ShowDialog(owner) : f.ShowDialog();
            }
        }

        /// <summary>Same shape as MessageBox.Show. OK → OK; OKCancel / YesNo → OK|Cancel / Yes|No.</summary>
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            bool two = buttons == MessageBoxButtons.OKCancel || buttons == MessageBoxButtons.YesNo;
            bool yesNo = buttons == MessageBoxButtons.YesNo;
            var r = Run(owner, new FxMessage(caption, text, Kind(icon), yesNo ? L.T("Да") : "OK", two ? (yesNo ? L.T("Нет") : L.T("Отмена")) : null, false, null));
            if (yesNo) return r == DialogResult.OK ? DialogResult.Yes : DialogResult.No;
            return r;
        }

        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            return Show(null, text, caption, buttons, icon);
        }

        /// <summary>Asks for a drive letter. Returns false on cancel; letter '\0' = remove the letter.</summary>
        public static bool AskLetter(IWin32Window owner, PartInfo p, out char letter)
        {
            letter = p.Letter;
            var free = VolumeOps.FreeLetters(p.Letter);
            var items = new System.Collections.Generic.List<string> { L.T("Без буквы (скрыть том)") };
            items.AddRange(free.Select(c => c + ":" + (c == p.Letter ? L.T("  (текущая)") : "")));
            var combo = new ThemedCombo(items.ToArray());
            int cur = p.Letter == '\0' ? 0 : free.IndexOf(char.ToUpperInvariant(p.Letter)) + 1;
            combo.SelectedIndex = Math.Max(0, cur);

            string name = (p.Letter != '\0' ? p.Letter + ": " : "") + (p.Label.Length > 0 ? p.Label : p.Type) + ", " + DiskService.FormatSize(p.Size);
            var f = new FxMessage(L.T("Изменить букву тома"), name + "\n" + L.T("Программы, которые ссылаются на старую букву, могут перестать находить свои файлы."),
                BannerKind.Info, L.T("Применить"), L.T("Отмена"), false, "");
            var root = (Stack)f.Controls[0];
            root.Controls.Add(new Row { MinHeight = 36 }.Add(new TextBlock(L.T("Новая буква"), TStyle.Body) { Single = true }, 120).Add(combo, -1));
            root.Controls.SetChildIndex(root.Controls[root.Controls.Count - 1], 1);
            int w = f.ClientSize.Width;
            f.ClientSize = new Size(w, root.HeightFor(w));
            if (Run(owner, f) != DialogResult.OK) return false;
            letter = combo.SelectedIndex <= 0 ? '\0' : free[combo.SelectedIndex - 1];
            return letter != p.Letter;
        }

        /// <summary>Two-button confirmation; returns true when the user accepts.</summary>
        public static bool Confirm(IWin32Window owner, string caption, string text, string okText, bool danger)
        {
            return Run(owner, new FxMessage(caption, text, danger ? BannerKind.Danger : BannerKind.Info, okText, L.T("Отмена"), danger,
                danger ? "" : null)) == DialogResult.OK;
        }
    }
}
