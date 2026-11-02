using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RouteLens
{
    // The monitor belongs to MainForm. Closing this view neither stops observation
    // nor writes the user's connection data to disk.
    public sealed class InvestigationDialog : Form
    {
        private readonly InvestigationMonitor _monitor;
        private readonly Timer _timer = new Timer();
        private readonly CheckBox _enabled, _includePaths, _includeTargets, _includeNote;
        private readonly NumericUpDown _threshold, _seconds;
        private readonly TextBox _search, _facts, _targets, _note;
        private readonly ComboBox _filter, _review;
        private readonly DataGridView _grid;
        private readonly Label _state, _count, _empty, _feedback;
        private readonly Button _save, _export, _apply;
        private readonly InvestigationTrend _trend;
        private readonly Dictionary<string, ReviewDraft> _drafts = new Dictionary<string, ReviewDraft>();
        private string _selectedId;
        private long _revision = -1;
        private bool _refreshing, _loadingReview, _shown;
        private static readonly string[] ReviewKeys = { "unreviewed", "investigating", "expected", "needs-more-evidence" };
        private static readonly string[] ReviewLabels = { "待核查", "正在调查", "已解释用途", "需要更多证据" };

        private sealed class ReviewDraft
        {
            public string Status, Note;
            public bool Dirty;
        }

        public InvestigationDialog(InvestigationMonitor monitor, Action showEvidence, Action showSystemTimeline, Action showExternal)
        {
            if (monitor == null) throw new ArgumentNullException("monitor");
            _monitor = monitor;
            Text = "行为调查 · RouteLens"; StartPosition = FormStartPosition.CenterParent;
            BackColor = UiPalette.Canvas; ForeColor = UiPalette.Ink; Font = UiPalette.Body(9);
            ClientSize = new Size(1120, 760); MinimumSize = new Size(880, 620);
            Padding = new Padding(22, 16, 22, 14); ShowInTaskbar = false; MinimizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); KeyPreview = true;

            TableLayoutPanel layout = Table(1, 8);
            foreach (int height in new[] { 36, 38, 82, 34, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            Controls.Add(layout);
            layout.Controls.Add(Label("行为调查", 20, UiPalette.Ink), 0, 0);
            layout.Controls.Add(Label("保留可核查的网络线索。流量阈值不会判定感染或 DDoS；没有记录也不代表电脑安全。", 9, UiPalette.Muted), 0, 1);

            RoundedPanel settingsCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(12, 5, 12, 5), Radius = 12 };
            TableLayoutPanel settings = Table(1, 2); settings.BackColor = UiPalette.Surface;
            settings.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); settings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            FlowLayoutPanel config = Flow(); config.BackColor = UiPalette.Surface;
            _enabled = Check("启用按程序组观察", 172); _enabled.Checked = monitor.Enabled;
            _threshold = Number(.001m, 10240m, 3, 88, "持续上传阈值，MiB 每秒");
            _threshold.Value = Clamp((decimal)(monitor.ThresholdBytesPerSecond / 1048576d), _threshold);
            _seconds = Number(1, 3600, 0, 68, "持续上传所需秒数"); _seconds.Value = Clamp((decimal)monitor.RequiredSeconds, _seconds);
            _apply = Button("应用设置", 100); _apply.Click += delegate { ApplySettings(); };
            config.Controls.AddRange(new Control[] { _enabled, InlineLabel("上传 ≥", 58), _threshold, InlineLabel("MiB/s，持续", 92), _seconds, InlineLabel("秒", 28), _apply });
            settings.Controls.Add(config, 0, 0);
            settings.Controls.Add(Label("默认关闭 · 仅本次运行 · 只检查 Clash 可计速连接 · 与“提醒”中的合计流量规则分别设置", 8.5f, UiPalette.Muted), 0, 1);
            settingsCard.Controls.Add(settings); layout.Controls.Add(settingsCard, 0, 2);
            _state = Label("", 9, UiPalette.Accent); _state.AccessibleName = "行为观察实际状态"; layout.Controls.Add(_state, 0, 3);

            TableLayoutPanel filters = Table(4, 1);
            filters.ColumnStyles.Clear(); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148)); filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
            filters.Controls.Add(Label("搜索", 9, UiPalette.Muted), 0, 0);
            _search = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 10, 4), AccessibleName = "搜索调查记录的程序、路径或目标", MaxLength = 256 };
            _filter = Combo("按复核状态筛选"); _filter.Items.Add("全部复核状态"); _filter.Items.AddRange(ReviewLabels); _filter.SelectedIndex = 0;
            _count = Label("0 条记录", 9, UiPalette.Muted); _count.TextAlign = ContentAlignment.MiddleRight;
            filters.Controls.Add(_search, 1, 0); filters.Controls.Add(_filter, 2, 0); filters.Controls.Add(_count, 3, 0); layout.Controls.Add(filters, 0, 4);

            TableLayoutPanel workspace = Table(2, 1); workspace.ColumnStyles.Clear();
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44)); workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            RoundedPanel listCard = new RoundedPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Padding = new Padding(1), Radius = 12 };
            _grid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = UiPalette.Surface, BorderStyle = BorderStyle.None, RowHeadersVisible = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, ReadOnly = true, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, EnableHeadersVisualStyles = false,
                ColumnHeadersHeight = 34, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = UiPalette.Border, AccessibleName = "行为调查记录列表" };
            _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Raised, ForeColor = UiPalette.Muted, Font = UiPalette.Body(8.5f), Padding = new Padding(5, 0, 0, 0) };
            _grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, SelectionBackColor = UiPalette.AccentSoft, SelectionForeColor = UiPalette.Ink,
                Font = UiPalette.Body(8.5f), Padding = new Padding(5, 0, 0, 0), NullValue = "未提供" };
            _grid.RowTemplate.Height = 40;
            AddColumn("开始", 17, 56); AddColumn("程序", 32, 80); AddColumn("结论", 29, 86); AddColumn("复核", 22, 76);
            _empty = Label("尚无调查记录", 11, UiPalette.Muted); _empty.TextAlign = ContentAlignment.MiddleCenter; _empty.AutoEllipsis = false;
            _empty.Padding = new Padding(18); _empty.BackColor = UiPalette.Surface;
            listCard.Controls.Add(_grid); listCard.Controls.Add(_empty); workspace.Controls.Add(listCard, 0, 0);

            TabControl details = new TabControl { Dock = DockStyle.Fill, Margin = new Padding(0), AccessibleName = "调查记录详情" };
            TabPage observation = Page("观察详情"), targets = Page("目标与链路"), review = Page("复核记录");
            details.TabPages.AddRange(new[] { observation, targets, review });
            TableLayoutPanel factLayout = Table(1, 2); factLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 122)); factLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _trend = new InvestigationTrend { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 6), AccessibleName = "Clash 内层上传速率采样趋势" };
            _facts = ReadBox("所选调查记录的事实和局限"); factLayout.Controls.Add(_trend, 0, 0); factLayout.Controls.Add(_facts, 0, 1); observation.Controls.Add(factLayout);
            _targets = ReadBox("最近端点与有上传增量的目标组"); targets.Controls.Add(_targets);
            TableLayoutPanel reviewLayout = Table(1, 5);
            reviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); reviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            reviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); reviewLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); reviewLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            reviewLayout.Controls.Add(Label("复核只记录你的调查进展，不改变证据结论。", 9, UiPalette.Muted), 0, 0);
            _review = Combo("调查记录的人工复核状态"); _review.Items.AddRange(ReviewLabels); _review.SelectedIndex = 0; reviewLayout.Controls.Add(_review, 0, 1);
            reviewLayout.Controls.Add(Label("备注（最多 4,000 字；默认不导出）", 9, UiPalette.Muted), 0, 2);
            _note = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, MaxLength = 4000, Margin = new Padding(0), AccessibleName = "人工复核备注" };
            reviewLayout.Controls.Add(_note, 0, 3);
            _save = Button("保存复核", 110); _save.Margin = new Padding(0, 8, 0, 0); _save.Click += delegate { SaveReview(); }; reviewLayout.Controls.Add(_save, 0, 4); review.Controls.Add(reviewLayout);
            workspace.Controls.Add(details, 1, 0); layout.Controls.Add(workspace, 0, 5);

            FlowLayoutPanel privacy = Flow();
            privacy.Controls.Add(InlineLabel("导出包含：", 90));
            _includePaths = Check("程序路径", 110); _includeTargets = Check("端点与代理链", 140); _includeNote = Check("人工备注", 110);
            privacy.Controls.AddRange(new Control[] { _includePaths, _includeTargets, _includeNote });
            privacy.Controls.Add(InlineLabel("默认隐去；备注可能含敏感信息", 258)); layout.Controls.Add(privacy, 0, 6);
            TableLayoutPanel bottom = Table(2, 1); bottom.ColumnStyles.Clear(); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            FlowLayoutPanel actions = Flow();
            _export = Button("导出此记录", 110); _export.Click += delegate { ExportRecord(); };
            Button evidence = Button("采集状态", 94); evidence.Enabled = showEvidence != null; evidence.Click += delegate { if (showEvidence != null) showEvidence(); };
            Button system = Button("系统变更", 94); system.Enabled = showSystemTimeline != null; system.Click += delegate { if (showSystemTimeline != null) showSystemTimeline(); };
            Button external = Button("外部记录", 94); external.Enabled = showExternal != null; external.Click += delegate { if (showExternal != null) showExternal(); };
            _feedback = InlineLabel("", 260); _feedback.ForeColor = UiPalette.Accent; _feedback.AccessibleName = "操作结果";
            actions.Controls.AddRange(new Control[] { _export, evidence, system, external, _feedback });
            Button close = Button("关闭", 80); close.DialogResult = DialogResult.Cancel; close.Margin = new Padding(8, 0, 0, 0);
            bottom.Controls.Add(actions, 0, 0); bottom.Controls.Add(close, 1, 0); layout.Controls.Add(bottom, 0, 7); CancelButton = close;

            _search.TextChanged += delegate { RefreshData(true); }; _filter.SelectedIndexChanged += delegate { RefreshData(true); };
            _grid.SelectionChanged += delegate { if (!_refreshing) SelectCurrent(); };
            _review.SelectedIndexChanged += delegate { UpdateDraft(); }; _note.TextChanged += delegate { UpdateDraft(); };
            _enabled.CheckedChanged += delegate { ConfigurationChanged(); }; _threshold.ValueChanged += delegate { ConfigurationChanged(); }; _seconds.ValueChanged += delegate { ConfigurationChanged(); };
            _threshold.Enabled = _seconds.Enabled = _enabled.Checked;
            _timer.Interval = 1000; _timer.Tick += delegate { RefreshData(false); };
            Shown += delegate { _shown = true; RefreshData(true); _timer.Start(); }; FormClosed += delegate { _timer.Stop(); };
            VisibleChanged += delegate
            {
                if (!_shown || IsDisposed || Disposing) return;
                if (Visible) { RefreshData(true); _timer.Start(); } else _timer.Stop();
            };
        }

        private void ApplySettings()
        {
            _threshold.Validate(); _seconds.Validate();
            try { _monitor.Configure(_enabled.Checked, (double)_threshold.Value * 1048576d, (double)_seconds.Value); }
            catch (ArgumentException) { _feedback.Text = "请检查阈值和持续时间。"; return; }
            _apply.Text = "已应用"; _feedback.Text = "设置仅在本次运行有效。"; RefreshData(true);
        }
        private void ConfigurationChanged()
        { _apply.Text = "应用设置"; _threshold.Enabled = _seconds.Enabled = _enabled.Checked; }

        private void RefreshData(bool force)
        {
            _state.Text = "当前：" + _monitor.State + "  ·  全机覆盖率未知  ·  记录仅保存在本次运行的内存中";
            if (!force && _revision == _monitor.Revision) return;
            _revision = _monitor.Revision;
            InvestigationCase[] all = _monitor.Snapshot();
            HashSet<string> retained = new HashSet<string>(); foreach (InvestigationCase item in all) retained.Add(item.Id);
            List<string> removed = new List<string>(); foreach (string key in _drafts.Keys) if (!retained.Contains(key)) removed.Add(key);
            foreach (string key in removed)
            {
                if (_drafts[key].Dirty) _feedback.Text = "旧记录已被淘汰，其复核草稿已移除。";
                _drafts.Remove(key);
            }
            string previous = _selectedId; int first = _grid.FirstDisplayedScrollingRowIndex;
            _refreshing = true; _grid.Rows.Clear();
            foreach (InvestigationCase item in all)
            {
                if (!Matches(item)) continue;
                int index = _grid.Rows.Add(item.OpenedAtUtc == default(DateTime) ? "未知" : item.OpenedAtUtc.ToLocalTime().ToString("HH:mm:ss"), item.ProgramName, item.Conclusion, ReviewText(item.ReviewStatus));
                DataGridViewRow row = _grid.Rows[index]; row.Tag = item.Id;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = Time(item.OpenedAtUtc) + "\r\n" + item.ProgramName + "\r\n" + item.Conclusion + (item.IsActive ? " · 持续中" : "") + "\r\n" + ReviewText(item.ReviewStatus);
            }
            int selected = -1;
            for (int i = 0; i < _grid.Rows.Count; i++) if (String.Equals(_grid.Rows[i].Tag as string, previous, StringComparison.Ordinal)) { selected = i; break; }
            if (_grid.Rows.Count > 0)
            {
                if (selected < 0) selected = 0;
                _grid.CurrentCell = _grid.Rows[selected].Cells[0]; _grid.Rows[selected].Selected = true;
                if (first >= 0 && first < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = first;
            }
            _refreshing = false;
            _empty.Visible = _grid.Rows.Count == 0;
            if (_empty.Visible)
            {
                _empty.Text = all.Length == 0 ? "尚无调查记录\r\n\r\n启用观察并应用设置，满足条件后会生成记录。\r\n\r\n也可在主窗口选中连接，使用“加入调查”保留快照。\r\n\r\n没有记录不能排除异常或潜伏程序。" : "没有匹配的记录\r\n\r\n请调整搜索或复核筛选条件。";
                _empty.BringToFront();
            }
            _count.Text = _grid.Rows.Count + " / " + all.Length + " 条" + (_monitor.EvictedCount > 0 ? " · 已淘汰 " + _monitor.EvictedCount : "");
            SelectCurrent();
        }
        public void SelectRecord(string id)
        {
            RefreshData(true);
            foreach (DataGridViewRow row in _grid.Rows) if (String.Equals(row.Tag as string, id, StringComparison.Ordinal))
            { _grid.CurrentCell = row.Cells[0]; row.Selected = true; SelectCurrent(); break; }
        }
        private bool Matches(InvestigationCase item)
        {
            if (_filter.SelectedIndex > 0 && item.ReviewStatus != ReviewKeys[_filter.SelectedIndex - 1]) return false;
            string query = _search.Text.Trim(); if (query.Length == 0) return true;
            if (Contains(item.ProgramName, query) || Contains(item.ProgramPath, query) || Contains(item.Conclusion, query) || Contains(item.Source, query)) return true;
            if (item.Targets != null) foreach (InvestigationTarget target in item.Targets)
                if (Contains(target.Host, query) || Contains(target.Address, query)) return true;
            return false;
        }
        private void SelectCurrent()
        {
            string id = _grid.CurrentRow == null ? null : _grid.CurrentRow.Tag as string;
            InvestigationCase item = id == null ? null : _monitor.Find(id);
            bool changed = !String.Equals(_selectedId, id, StringComparison.Ordinal); _selectedId = item == null ? null : id;
            _export.Enabled = _save.Enabled = _review.Enabled = _note.Enabled = item != null;
            _trend.SetCase(item);
            if (item == null)
            {
                _facts.Text = "选择一条记录，查看事实、归因范围和采集缺口。\r\n\r\n本页自动规则只生成“已观察到异常”的待核查线索；手动快照标记为“证据不足”。\r\n\r\n“存在可疑证据”需要结合其他证据；确认攻击行为需要更直接的行为证据与独立核查。本页不会自动升级结论。";
                _targets.Text = "没有选中的调查记录。";
                _loadingReview = true; _note.Text = ""; _review.SelectedIndex = 0; _loadingReview = false; return;
            }
            string facts = BuildFacts(item), targets = BuildTargets(item);
            if (_facts.Text != facts && (changed || !_facts.Focused)) _facts.Text = facts;
            if (_targets.Text != targets && (changed || !_targets.Focused)) _targets.Text = targets;
            if (changed)
            {
                ReviewDraft draft;
                if (!_drafts.TryGetValue(id, out draft)) { draft = new ReviewDraft { Status = item.ReviewStatus, Note = item.ReviewNote ?? "" }; _drafts[id] = draft; }
                _loadingReview = true; _note.Text = draft.Note; _review.SelectedIndex = Math.Max(0, Array.IndexOf(ReviewKeys, draft.Status)); _loadingReview = false;
                _save.Text = draft.Dirty ? "保存复核 *" : "保存复核";
            }
        }
        private void UpdateDraft()
        {
            if (_loadingReview || _selectedId == null || _review.SelectedIndex < 0) return;
            ReviewDraft draft;
            if (!_drafts.TryGetValue(_selectedId, out draft)) return;
            draft.Status = ReviewKeys[_review.SelectedIndex]; draft.Note = _note.Text; draft.Dirty = true; _save.Text = "保存复核 *";
        }
        private void SaveReview()
        {
            if (_selectedId == null) return;
            ReviewDraft draft;
            if (!_drafts.TryGetValue(_selectedId, out draft)) return;
            if (_monitor.Find(_selectedId) == null) { _feedback.Text = "记录已超出保留上限。"; RefreshData(true); return; }
            _monitor.SetReview(_selectedId, draft.Status, draft.Note); draft.Dirty = false; _save.Text = "保存复核";
            _feedback.Text = "已保存复核；证据结论未改变。"; RefreshData(true);
        }
        private void ExportRecord()
        {
            if (_selectedId == null) return;
            string id = _selectedId; ReviewDraft draft;
            if (_drafts.TryGetValue(id, out draft) && draft.Dirty)
            { MessageBox.Show(this, "请先保存复核，确保导出包含当前已保存的复核状态。", "复核尚未保存", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Title = "导出调查记录（请检查后再分享）", Filter = "JSON 调查记录 (*.json)|*.json", DefaultExt = "json", AddExtension = true, FileName = "RouteLens-调查记录-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                if (_monitor.Find(id) == null) { _feedback.Text = "记录已超出保留上限，请重新选择。"; RefreshData(true); return; }
                try
                {
                    string json = _monitor.BuildExportJson(id, _includePaths.Checked, _includeTargets.Checked, _includeNote.Checked);
                    File.WriteAllText(dialog.FileName, json, new UTF8Encoding(false)); _feedback.Text = "已导出；分享前请检查内容。";
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException)) throw;
                    MessageBox.Show(this, "文件未能写入，请检查目标位置是否可写。", "导出失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private static string BuildFacts(InvestigationCase item)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("结论：" + item.Conclusion).AppendLine("复核：" + ReviewText(item.ReviewStatus) + "（人工记录）").AppendLine();
            text.AppendLine("来源：" + item.Source).AppendLine("程序组：" + Value(item.ProgramName)).AppendLine("路径：" + Value(item.ProgramPath));
            List<string> pids = new List<string>(); if (item.ObservedPids != null) foreach (int pid in item.ObservedPids) if (pid > 0) pids.Add(pid.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("曾观察到的 PID：" + (pids.Count == 0 ? "未提供" : String.Join("、", pids.ToArray())) + (item.PidsTruncated ? "（已截断）" : ""));
            text.AppendLine("归因：" + item.Attribution).AppendLine("完整性：" + item.Coverage).AppendLine();
            text.AppendLine("开始：" + Time(item.OpenedAtUtc));
            text.AppendLine("最近：" + Time(item.LastSeenAtUtc));
            if (item.Kind == "sustained-upload")
            {
                text.AppendLine("规则：上传 ≥ " + Rate(item.ThresholdBytesPerSecond) + "，持续 ≥ " + item.RequiredSeconds.ToString("0.#") + " 秒");
                text.AppendLine("已观察持续：" + item.DurationSeconds.ToString("0.#") + " 秒；峰值：" + Rate(item.PeakUploadBytesPerSecond));
                text.AppendLine("最近计速连接：" + item.MeasuredConnections + " / " + item.ObservedConnections + "（该程序组，不是全机覆盖率）");
                text.AppendLine("观察状态：" + (item.IsActive ? "持续中" : Value(item.EndReason)));
            }
            else
            {
                text.AppendLine("快照累计上传：" + Bytes(item.CapturedUploadBytes));
                text.AppendLine("快照累计下载：" + Bytes(item.CapturedDownloadBytes));
                text.AppendLine("来源报告的归因：" + Value(item.ReportedAttribution));
                text.AppendLine("以上是来源报告的累计值与归因描述，没有据此推算速率、持续时长或确认进程身份。");
            }
            if (item.SamplesTruncated) text.AppendLine("趋势仅保留最近一段采样，早期样本已截断。");
            text.AppendLine().AppendLine("需要进一步核查");
            text.AppendLine("结合程序用途、进程实例与系统变更；PID 或路径相同并不能证明是同一个进程实例。");
            text.AppendLine("短连接、绕过 Clash 的活动与未识别程序可能遗漏。未观察到异常不能排除潜伏程序。");
            text.AppendLine("本机多个来源并非独立可信根；必要时使用路由器或独立传感器验证。阈值不会自动确认感染、攻击或分布式参与。");
            return text.ToString();
        }
        private static string BuildTargets(InvestigationCase item)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("有上传增量的目标组"); int positive = 0;
            if (item.Targets != null) foreach (InvestigationTarget target in item.Targets) if (target.PositiveUpload)
            { text.AppendLine("• " + Value(target.Host)); positive++; }
            if (positive == 0) text.AppendLine("未取得目标组上传增量证据。");
            text.AppendLine("以上仅证明该程序与目标名分组中存在上传增量，未归因到具体端点，也未证明它贡献了本记录的全部上传量。").AppendLine();
            text.AppendLine("本记录观察期间出现的端点候选"); int recent = 0;
            if (item.Targets != null) foreach (InvestigationTarget target in item.Targets) if (!target.PositiveUpload)
            {
                text.AppendLine("• " + Value(target.Host));
                text.AppendLine("  " + Value(target.Protocol) + "  远端：" + Endpoint(target.Address, target.Port));
                text.AppendLine("  本地：" + Endpoint(target.LocalAddress, target.LocalPort));
                text.AppendLine("  代理链：" + Value(target.ProxyChain)); recent++;
            }
            if (recent == 0) text.AppendLine("没有可用的端点信息。");
            if (item.TargetsTruncated) text.AppendLine("目标列表已达到保留上限，未展示全部目标。");
            text.AppendLine().AppendLine("候选端点不是上传归因结果。域名、代理链来自连接元数据，不能据此确认远端身份。");
            text.AppendLine().AppendLine("VPN / Clash 归因边界");
            text.AppendLine("本记录属于来源可见的连接层。Clash 内层连接、VPN 隧道外层与物理网卡不能直接相加；当前未建立它们之间的逐连接对应关系。");
            text.AppendLine("复用隧道、fake-IP 和加密流量不能仅凭时间或相近字节数还原最终目标。外部记录需单独核查来源与采集位置。");
            return text.ToString();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { _search.Focus(); _search.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { if (_save.Enabled) SaveReview(); return true; }
            if (keyData == (Keys.Control | Keys.E)) { if (_export.Enabled) ExportRecord(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        public bool ConfirmDiscardDrafts()
        {
            bool dirty = false; foreach (ReviewDraft draft in _drafts.Values) if (draft.Dirty) { dirty = true; break; }
            return !dirty || MessageBox.Show(TopLevelControl ?? this, "还有未保存的复核备注。关闭后这些修改会丢失，是否仍要关闭？", "未保存的复核", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!ConfirmDiscardDrafts()) e.Cancel = true;
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing) { if (disposing) _timer.Dispose(); base.Dispose(disposing); }

        private static TableLayoutPanel Table(int columns, int rows)
        { TableLayoutPanel table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = new Padding(0) }; for (int i = 0; i < columns; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns)); return table; }
        private static FlowLayoutPanel Flow() { return new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 3, 0, 0) }; }
        private static Label Label(string text, float size, Color color) { return new Label { Text = text, Font = UiPalette.Body(size), ForeColor = color, Dock = DockStyle.Fill, Margin = new Padding(0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }; }
        private static Label InlineLabel(string text, int width) { Label label = Label(text, 9, UiPalette.Muted); label.Dock = DockStyle.None; label.Size = new Size(width, 30); return label; }
        private static CheckBox Check(string text, int width) { return new ModernCheck { Text = text, Width = width, Height = 30, Margin = new Padding(0, 0, 5, 0), AccessibleName = text }; }
        private static Button Button(string text, int width) { return new ModernButton { Text = text, Width = width, Height = 34, Margin = new Padding(0, 0, 8, 0), AccessibleName = text }; }
        private static NumericUpDown Number(decimal minimum, decimal maximum, int decimals, int width, string name) { return new NumericUpDown { Minimum = minimum, Maximum = maximum, DecimalPlaces = decimals, Increment = decimals > 0 ? .1m : 1m, Width = width, Margin = new Padding(0, 3, 7, 0), AccessibleName = name }; }
        private static decimal Clamp(decimal value, NumericUpDown input) { return Math.Max(input.Minimum, Math.Min(input.Maximum, value)); }
        private static ComboBox Combo(string name) { return new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 4, 0, 5), AccessibleName = name }; }
        private static TextBox ReadBox(string name) { return new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None, BackColor = UiPalette.Surface, ForeColor = UiPalette.Ink, Font = UiPalette.Body(9), AccessibleName = name, Margin = new Padding(0) }; }
        private static TabPage Page(string text) { return new TabPage(text) { BackColor = UiPalette.Surface, Padding = new Padding(12) }; }
        private void AddColumn(string header, float weight, int minimum) { _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = header, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable }); }
        private static string ReviewText(string value) { int index = Array.IndexOf(ReviewKeys, value); return index < 0 ? "待核查" : ReviewLabels[index]; }
        private static bool Contains(string value, string query) { return value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0; }
        private static string Value(string value) { return String.IsNullOrWhiteSpace(value) ? "未提供" : value; }
        private static string Endpoint(string address, int port) { return String.IsNullOrWhiteSpace(address) ? "未提供" : (address.IndexOf(':') >= 0 ? "[" + address + "]" : address) + (port > 0 ? ":" + port : ""); }
        private static string Time(DateTime value) { return value == default(DateTime) ? "未知" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz"); }
        private static string Bytes(long? value) { return value.HasValue ? value.Value.ToString("N0", CultureInfo.CurrentCulture) + " 字节" : "未提供有效计数"; }
        internal static string Rate(double value) { return (value / 1048576d).ToString("0.###", CultureInfo.CurrentCulture) + " MiB/s"; }
    }

    internal sealed class InvestigationTrend : Control
    {
        private InvestigationCase _item;
        internal InvestigationTrend() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); BackColor = UiPalette.Surface; }
        internal void SetCase(InvestigationCase item) { _item = item; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Graphics g = e.Graphics;
            using (Font font = UiPalette.Body(8)) using (Brush muted = new SolidBrush(UiPalette.Muted))
            {
                g.DrawString(_item != null && _item.Kind == "connection-snapshot" ? "连接快照 · 无连续速率" : "Clash 内层上传 · 采样速率", font, muted, 0, 0);
                if (_item == null || _item.Kind != "sustained-upload" || _item.Samples == null || _item.Samples.Count < 2)
                { g.DrawString("没有可绘制的连续速率样本", font, muted, 0, 45); return; }
                double peak = 0; foreach (InvestigationSample sample in _item.Samples) if (sample.UploadBytesPerSecond.HasValue && !Double.IsNaN(sample.UploadBytesPerSecond.Value) && !Double.IsInfinity(sample.UploadBytesPerSecond.Value)) peak = Math.Max(peak, sample.UploadBytesPerSecond.Value);
                RectangleF plot = new RectangleF(0, 31, Math.Max(1, Width - 4), Math.Max(1, Height - 53));
                DateTime start = _item.Samples[0].TimeUtc, end = _item.Samples[_item.Samples.Count - 1].TimeUtc;
                double seconds = (end - start).TotalSeconds;
                if (seconds <= 0) return;
                g.DrawString(InvestigationDialog.Rate(peak), font, muted, Math.Max(0, Width - 125), 0);
                using (Pen grid = new Pen(UiPalette.Border)) { g.DrawLine(grid, plot.Left, plot.Bottom, plot.Right, plot.Bottom); g.DrawLine(grid, plot.Left, plot.Top, plot.Right, plot.Top); }
                PointF? previous = null; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen line = new Pen(UiPalette.Accent, 2)) foreach (InvestigationSample sample in _item.Samples)
                {
                    if (!sample.UploadBytesPerSecond.HasValue || Double.IsNaN(sample.UploadBytesPerSecond.Value) || Double.IsInfinity(sample.UploadBytesPerSecond.Value)) { previous = null; continue; }
                    float x = plot.Left + (float)Math.Max(0, Math.Min(1, (sample.TimeUtc - start).TotalSeconds / seconds)) * plot.Width;
                    float y = plot.Bottom - (float)Math.Max(0, Math.Min(1, sample.UploadBytesPerSecond.Value / Math.Max(1, peak))) * plot.Height;
                    PointF point = new PointF(x, y); if (previous.HasValue) g.DrawLine(line, previous.Value, point); previous = point;
                }
                g.DrawString(start.ToLocalTime().ToString("HH:mm:ss"), font, muted, plot.Left, plot.Bottom + 3);
                string last = end.ToLocalTime().ToString("HH:mm:ss"); SizeF measured = g.MeasureString(last, font); g.DrawString(last, font, muted, Math.Max(0, Width - measured.Width), plot.Bottom + 3);
            }
        }
    }
}
