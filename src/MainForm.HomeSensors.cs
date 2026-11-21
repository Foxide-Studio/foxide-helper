using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>"My sensors" on Home: any numeric sensor from the component pages pinned as a live tile.</summary>
    public partial class MainForm
    {
        // page id → (page title, provider)
        List<KeyValuePair<string, KeyValuePair<string, Func<List<SensorRow>>>>> SensorPages()
        {
            var l = new List<KeyValuePair<string, KeyValuePair<string, Func<List<SensorRow>>>>>();
            Action<string, string, Func<List<SensorRow>>> add = (id, t, f) => l.Add(new KeyValuePair<string, KeyValuePair<string, Func<List<SensorRow>>>>(id, new KeyValuePair<string, Func<List<SensorRow>>>(t, f)));
            add("cpu", L.T("Процессор"), CpuSensorRows);
            add("gpu", L.T("Видеокарта"), GpuSensorRows);
            add("ram", L.T("Оперативная память"), RamSensorRows);
            add("cooling", L.T("Охлаждение"), CoolingSensorRows);
            if (BatteryService.HasBattery) add("battery", L.T("Аккумулятор"), BatterySensorRows);
            return l;
        }

        static string SensorKey(string page, SensorRow r) { return page + "|" + (r.Id ?? r.Group + "|" + r.Name); }

        static List<string> HomeSensorKeys()
        {
            return (Settings.HomeSensors ?? "").Split(new[] { ";;" }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        static void SaveHomeSensorKeys(IEnumerable<string> keys)
        {
            Settings.HomeSensors = string.Join(";;", keys.Distinct().ToArray());
            Settings.Save();
        }

        static string FormatSensor(SensorRow r)
        {
            if (!r.Value.HasValue) return r.Text ?? "—";
            return r.Value.Value.ToString(r.Format) + (string.IsNullOrEmpty(r.Unit) ? "" : " " + r.Unit);
        }

        /// <summary>The "My sensors" card on Home.</summary>
        Control HomeSensorsCard()
        {
            var card = Card(L.T("Мои датчики"), L.T("Любые датчики со страниц компонентов — нажмите «Добавить датчики» и отметьте нужные"));
            var grid = new TileGrid { MinTile = 200, TileH = 96 };
            var empty = new TextBlock(L.T("Пока ничего не добавлено."), TStyle.Muted);
            var addBtn = new FlatButton(L.T("Добавить датчики"), BtnKind.Secondary, "");
            addBtn.Click += (s, e) => { if (ShowSensorPicker()) BuildHome(); };
            card.Controls.Add(grid);
            card.Controls.Add(empty);
            card.Controls.Add(new Row { MinHeight = 38 }.Add(addBtn, 230).Add(new Panel(), -1));

            var keys = HomeSensorKeys();
            empty.Visible = keys.Count == 0;
            grid.Visible = keys.Count > 0;
            var tiles = new Dictionary<string, StatTile>();
            foreach (var k in keys)
            {
                var parts = k.Split('|');
                if (parts.Length < 2) continue;
                var t = new StatTile("…", "—", null) { Cursor = Cursors.Hand };
                string page = parts[0];
                t.Click += (s, e) => { Telemetry.Count("home.sensor"); SelectSection(page); };
                tiles[k] = t;
                grid.Controls.Add(t);
            }
            if (tiles.Count == 0) return card;

            Action refresh = () =>
            {
                var pagesNeeded = new HashSet<string>(tiles.Keys.Select(k => k.Split('|')[0]));
                foreach (var p in SensorPages().Where(x => pagesNeeded.Contains(x.Key)))
                {
                    List<SensorRow> rows;
                    try { rows = p.Value.Value() ?? new List<SensorRow>(); } catch { rows = new List<SensorRow>(); }
                    foreach (var r in rows)
                    {
                        StatTile t;
                        if (!tiles.TryGetValue(SensorKey(p.Key, r), out t)) continue;
                        if (t.Title != r.Name) { t.Title = r.Name; }
                        t.Set(FormatSensor(r), r.Group);
                        double? v = r.Value;
                        if (r.Unit == "°C") t.ValueColor = TempColor(v, 75, 88);
                        else t.ValueColor = null;
                    }
                }
            };
            var timer = new Timer { Interval = 2000 };
            timer.Tick += (s, e) => { if (section == "home" && Visible) refresh(); };
            timer.Start();
            card.Disposed += (s, e) => timer.Dispose();
            StartHwPoller();
            refresh();
            return card;
        }

        /// <summary>Dialog with every available sensor grouped by page; toggles pin / unpin. True when something changed.</summary>
        string pickerShot;
        public void ShotPicker(string path) { pickerShot = path; try { ShowSensorPicker(); } finally { pickerShot = null; } }

        bool ShowSensorPicker()
        {
            var selected = new HashSet<string>(HomeSensorKeys());
            var before = new HashSet<string>(selected);
            using (var f = new Form())
            {
                f.Text = L.T("Мои датчики");
                f.FormBorderStyle = FormBorderStyle.Sizable;
                f.MinimizeBox = f.MaximizeBox = false;
                f.ShowInTaskbar = false;
                f.Icon = Icon;
                f.StartPosition = FormStartPosition.CenterParent;
                f.AutoScaleMode = AutoScaleMode.None;
                f.Font = Theme.Body;
                f.ClientSize = new Size(Theme.Dp(620), Math.Min(Theme.Dp(720), Screen.FromControl(this).WorkingArea.Height - Theme.Dp(80)));
                f.MinimumSize = new Size(Theme.Dp(480), Theme.Dp(360));

                var list = new Stack { Pad = 20, Gap = 4, Dock = DockStyle.Fill, Bg = () => Theme.P.Back };
                list.AutoScrollEnabled = true;
                var bottom = new Stack { Pad = 14, Dock = DockStyle.Bottom, Bg = () => Theme.P.Back };
                var ok = new FlatButton(L.T("Готово"), BtnKind.Primary);
                ok.Click += (s, e) => f.DialogResult = DialogResult.OK;
                var none = new FlatButton(L.T("Убрать все"), BtnKind.Ghost);
                bottom.Controls.Add(new Row { MinHeight = 38 }.Add(none, 160).Add(new Panel(), -1).Add(ok, 140));
                bottom.Height = bottom.HeightFor(f.ClientSize.Width);

                list.Controls.Add(new TextBlock(L.T("Отметьте датчики, которые нужно показывать на «Главной». Значения обновляются каждые 2 секунды."), TStyle.Muted));
                var toggles = new List<Toggle>();
                int count = 0;
                foreach (var p in SensorPages())
                {
                    List<SensorRow> rows;
                    try { rows = p.Value.Value() ?? new List<SensorRow>(); } catch { rows = new List<SensorRow>(); }
                    rows = rows.Where(r => r.Value.HasValue).ToList();
                    if (rows.Count == 0) continue;
                    list.Controls.Add(new Panel { Height = Theme.Dp(8) });
                    list.Controls.Add(new TextBlock(p.Value.Key, TStyle.Section) { Single = true });
                    string grp = null;
                    foreach (var r in rows)
                    {
                        if (r.Group != grp) { grp = r.Group; list.Controls.Add(new TextBlock(grp, TStyle.Bold) { Single = true }); }
                        string key = SensorKey(p.Key, r);
                        var tg = new Toggle(r.Name + "  ·  " + FormatSensor(r), selected.Contains(key));
                        tg.CheckedChanged += (s, e) => { if (tg.Checked) selected.Add(key); else selected.Remove(key); };
                        toggles.Add(tg);
                        list.Controls.Add(tg);
                        count++;
                    }
                }
                if (count == 0) list.Controls.Add(new TextBlock(L.T("Датчики ещё не опрошены — подождите пару секунд и откройте окно снова."), TStyle.Muted));
                none.Click += (s, e) => { foreach (var t in toggles) t.Checked = false; selected.Clear(); };

                f.Controls.Add(list);
                f.Controls.Add(bottom);
                f.HandleCreated += (s, e) => { Theme.Apply(f); Theme.ApplyTitleBar(f); };
                if (pickerShot != null)
                {   // diagnostics (--shot picker): render the dialog to a PNG instead of showing it modally
                    f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-20000, -20000); f.Show(); Application.DoEvents();
                    using (var bmp = new Bitmap(f.Width, f.Height)) { f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height)); bmp.Save(pickerShot, System.Drawing.Imaging.ImageFormat.Png); }
                    f.Close();
                    return false;
                }
                if (f.ShowDialog(this) != DialogResult.OK) return false;
            }
            // keep the user's order: old keys first, new ones appended
            var keys = HomeSensorKeys().Where(selected.Contains).ToList();
            keys.AddRange(selected.Where(k => !keys.Contains(k)));
            SaveHomeSensorKeys(keys);
            Telemetry.Count("action.home_sensors");
            return !before.SetEquals(selected);
        }
    }
}
