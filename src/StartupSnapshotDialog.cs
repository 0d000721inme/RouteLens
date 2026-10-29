using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RouteLens
{
    public sealed class StartupSnapshotDialog : Form
    {
        private readonly Func<CancellationToken, StartupSnapshot> _read;
        private readonly Button _baselineButton, _compareButton;
        private readonly Label _status;
        private readonly TextBox _results;
        private StartupSnapshot _baseline;
        private CancellationTokenSource _running;
        public StartupSnapshotDialog() : this(StartupSnapshots.Read) { }
        internal StartupSnapshotDialog(Func<CancellationToken, StartupSnapshot> read)
        {
            if (read == null) throw new ArgumentNullException("read"); _read = read;
            Text = "启动项快照 · RouteLens"; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); Size = new Size(830, 620); MinimumSize = new Size(700, 500);
            Padding = new Padding(22); BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9.5f);
            TableLayoutPanel root = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), ColumnCount = 1, RowCount = 5 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 95)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); Controls.Add(root);
            root.Controls.Add(new Label { Text = "启动项快照", Dock = DockStyle.Fill, ForeColor = UiPalette.Ink, Font = UiPalette.Body(19), Margin = new Padding(0), AutoEllipsis = true }, 0, 0);
            root.Controls.Add(new Label { Text = "先建立基线，之后手动再次读取并比较当前用户和全机 Run 注册表项；只显示项名及变化，不显示命令内容。两个视图可能指向同一项。\r\n仅在本窗口内存保存，不上传、不写入文件；关闭窗口后基线消失。新增或变化不代表程序已运行，也不代表恶意。无法完整读取的来源不做比较。\r\n此功能不覆盖计划任务、服务、其他自启动位置或两次快照之间出现又消失的项目。", Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(9), ForeColor = UiPalette.Muted }, 0, 1);
            _status = new Label { Text = "尚未读取 · 请先建立基线", Dock = DockStyle.Fill, Margin = new Padding(0), Font = UiPalette.Body(9), ForeColor = UiPalette.Accent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            root.Controls.Add(_status, 0, 2);
            RoundedPanel card = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(14), Radius = 14 };
            _results = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9.5f), WordWrap = true, AccessibleName = "启动项来源状态及快照比较结果",
                Text = "等待建立基线。\r\n\r\n请在希望比较的两个时间点分别点击“建立基线”和“比较当前”。" };
            card.Controls.Add(_results); root.Controls.Add(card, 0, 3);
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0) };
            _baselineButton = new ModernButton { Text = "建立基线", Width = 125, Height = 38, Primary = true, Margin = new Padding(0, 0, 10, 0), AccessibleName = "手动建立本机启动项基线" };
            _compareButton = new ModernButton { Text = "比较当前", Width = 125, Height = 38, Enabled = false, Margin = new Padding(0, 0, 10, 0) };
            Button close = new ModernButton { Text = "关闭", Width = 88, Height = 38, Margin = new Padding(0), DialogResult = DialogResult.Cancel };
            actions.Controls.Add(_baselineButton); actions.Controls.Add(_compareButton); actions.Controls.Add(close); root.Controls.Add(actions, 0, 4); CancelButton = close;
            _baselineButton.Click += delegate { CaptureSnapshot(true); }; _compareButton.Click += delegate { CaptureSnapshot(false); };
            FormClosing += delegate { if (_running != null) _running.Cancel(); };
            Shown += delegate { _baselineButton.Focus(); };
        }
        private async void CaptureSnapshot(bool baseline)
        {
            if (_running != null) return;
            CancellationTokenSource running = new CancellationTokenSource(); _running = running;
            _baselineButton.Enabled = false; _compareButton.Enabled = false; _status.Text = "正在只读获取启动项…";
            try
            {
                StartupSnapshot snapshot = await Task.Run(delegate { return _read(running.Token); });
                if (IsDisposed || Disposing) return;
                if (snapshot == null || snapshot.Cancelled) { _status.Text = "读取取消或失败；保留先前基线。"; return; }
                StringBuilder text = new StringBuilder();
                if (baseline)
                {
                    _baseline = snapshot; _status.Text = "基线已建立 · " + snapshot.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                    text.AppendLine("基线来源状态：");
                    foreach (StartupSource source in snapshot.Sources.Values) text.AppendLine(source.Name + "：" + source.Status);
                    text.AppendLine(); text.AppendLine("准备好后点击“比较当前”。未成功读取的来源不会判断变化。");
                }
                else if (_baseline != null)
                {
                    StartupComparison comparison = StartupSnapshots.Compare(_baseline, snapshot);
                    _status.Text = "比较完成 · " + comparison.Changes.Count + " 项变化 · " + comparison.Unknown.Count + " 个来源无法判断";
                    text.AppendLine("基线：" + _baseline.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "  /  当前：" + snapshot.CapturedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                    text.AppendLine();
                    if (comparison.Changes.Count == 0) text.AppendLine("完整读取的来源没有观察到差异。");
                    else foreach (StartupChange item in comparison.Changes) text.AppendLine(item.Source + "  ·  " + item.Change + "  ·  " + item.Name);
                    text.AppendLine();
                    foreach (string unknown in comparison.Unknown) text.AppendLine(unknown);
                    text.AppendLine("仅比较两次读取到的 Run 注册表项；不证明执行或恶意。");
                }
                _results.Text = text.ToString();
            }
            catch (Exception) { if (!IsDisposed && !Disposing) _status.Text = "读取失败；保留先前基线。"; }
            finally
            {
                if (Object.ReferenceEquals(_running, running)) _running = null;
                running.Dispose();
                if (!IsDisposed && !Disposing) { _baselineButton.Enabled = true; _compareButton.Enabled = _baseline != null; }
            }
        }
    }
}
