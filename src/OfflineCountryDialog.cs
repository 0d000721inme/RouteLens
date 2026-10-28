using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class OfflineCountryDialog : Form
    {
        private readonly OfflineCountryDatabase database;
        private readonly Label state, detail, info;
        private readonly Button import, download;
        private readonly Timer refresh = new Timer();

        public OfflineCountryDialog(OfflineCountryDatabase database)
        {
            if (database == null) throw new ArgumentNullException("database");
            this.database = database;
            Text = "离线国家数据库 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            Size = new Size(780, 520); MinimumSize = new Size(700, 480); ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f); Padding = new Padding(24, 18, 24, 16);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            Controls.Add(layout);

            TableLayoutPanel heading = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 2 };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            heading.Controls.Add(MakeLabel("离线国家数据库", 20, UiPalette.Ink), 0, 0);
            heading.Controls.Add(MakeLabel("在本机识别 IP 地区，减少逐个联网查询", 9, UiPalette.Muted), 0, 1);
            layout.Controls.Add(heading, 0, 0);

            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(18, 11, 18, 11), Radius = 14 };
            TableLayoutPanel cardRows = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = UiPalette.Surface, ColumnCount = 1, RowCount = 3 };
            cardRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); cardRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); cardRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 29)); cardRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            cardRows.Controls.Add(MakeLabel("数据库状态", 9, UiPalette.Muted), 0, 0);
            state = MakeLabel("正在读取", 14, UiPalette.Ink); cardRows.Controls.Add(state, 0, 1);
            detail = MakeLabel("", 9, UiPalette.Muted); cardRows.Controls.Add(detail, 0, 2);
            card.Controls.Add(cardRows); layout.Controls.Add(card, 0, 1);

            info = MakeLabel("软件内置 DB-IP Lite 国家数据库，也可下载更新或导入国家版 CSV / CSV.GZ。数据库仅用于本机查询；导入时会先验证内容，失败时保留原数据库。\r\n\r\n没有离线结果时，可按主界面的“联网查国家”开关决定是否使用在线查询。地点是近似归属，不能作为服务器的精确位置。", 9.3f, UiPalette.Muted);
            info.AutoEllipsis = false; info.TextAlign = ContentAlignment.TopLeft; info.Padding = new Padding(2, 5, 2, 0); layout.Controls.Add(info, 0, 2);
            LinkLabel attribution = new LinkLabel { Dock = DockStyle.Fill, Margin = new Padding(2, 0, 0, 0), Text = "IP Geolocation by DB-IP · CC BY 4.0  来源与许可", Font = UiPalette.Body(8.8f), LinkColor = UiPalette.Accent, ActiveLinkColor = UiPalette.Accent, TextAlign = ContentAlignment.MiddleLeft };
            attribution.LinkClicked += delegate { try { Process.Start(new ProcessStartInfo("https://db-ip.com/db/lite.php") { UseShellExecute = true }); } catch (Exception) { } };
            layout.Controls.Add(attribution, 0, 3);

            TableLayoutPanel actions = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 4, RowCount = 1, Padding = new Padding(0, 7, 0, 0) };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            import = new ModernButton { Text = "导入 CSV / GZ", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), AccessibleName = "选择并导入 DB-IP 国家数据库" };
            download = new ModernButton { Text = "下载 DB-IP Lite", Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), Primary = true, AccessibleName = "联网下载 DB-IP Lite 国家数据库" };
            Button close = new ModernButton { Text = "关闭", Dock = DockStyle.Fill, Margin = new Padding(0), DialogResult = DialogResult.OK };
            import.Click += async delegate
            {
                using (OpenFileDialog picker = new OpenFileDialog { Title = "选择 DB-IP Country Lite 数据库", Filter = "国家数据库 (*.csv;*.csv.gz;*.gz)|*.csv;*.csv.gz;*.gz|所有文件 (*.*)|*.*", CheckFileExists = true, Multiselect = false })
                {
                    if (picker.ShowDialog(this) != DialogResult.OK) return;
                    try { await database.ImportAsync(picker.FileName); }
                    catch (Exception) { }
                    if (!IsDisposed) Render();
                }
            };
            download.Click += async delegate { try { await database.DownloadAsync(); } catch (Exception) { } if (!IsDisposed) Render(); };
            actions.Controls.Add(import, 0, 0); actions.Controls.Add(download, 1, 0); actions.Controls.Add(close, 3, 0); layout.Controls.Add(actions, 0, 4); CancelButton = close;
            refresh.Interval = 500; refresh.Tick += delegate { Render(); };
            Shown += delegate { Render(); refresh.Start(); };
            FormClosed += delegate { refresh.Stop(); };
            Render();
        }
        private static Label MakeLabel(string value, float size, Color color)
        { return new Label { Text = value, Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(size), ForeColor = color, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        private void Render()
        {
            if (IsDisposed || Disposing) return;
            OfflineCountryStatus status = database.GetStatus();
            state.Text = status.Busy ? "正在处理" : status.Ready ? "已启用" : "尚未安装";
            state.ForeColor = status.Busy || !status.Ready ? UiPalette.Warn : UiPalette.Accent;
            detail.Text = status.Message + (status.Ready ? " · " + status.RangeCount.ToString("N0") + " 个网段" + (status.InstalledAt == DateTime.MinValue ? "" : " · 安装时间 " + status.InstalledAt.ToString("yyyy-MM-dd HH:mm")) : "");
            import.Enabled = !status.Busy; download.Enabled = !status.Busy;
        }
        protected override void Dispose(bool disposing) { if (disposing) refresh.Dispose(); base.Dispose(disposing); }
    }
}
