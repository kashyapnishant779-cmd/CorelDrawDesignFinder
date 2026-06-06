using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CorelDrawDesignFinder
{
    public partial class FormMain : Form
    {
        private List<CdrFileInfo> _scannedFiles = new List<CdrFileInfo>();
        private List<DuplicateGroup> _duplicateGroups = new List<DuplicateGroup>();
        private CancellationTokenSource _cts;
        private CdrFileInfo _selectedFileInfo;

        public FormMain()
        {
            InitializeComponent();
            // Enable default values
            txtFolder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            numThreshold.Value = 5; // Hamming distance limit (0 - 8 is ideal for high similarity)
            
            // Configure Listview
            ConfigureListView();
        }

        private void ConfigureListView()
        {
            lstFiles.View = View.Details;
            lstFiles.FullRowSelect = true;
            lstFiles.GridLines = true;
            lstFiles.Columns.Clear();
            lstFiles.Columns.Add("File Name", 180);
            lstFiles.Columns.Add("File Path", 280);
            lstFiles.Columns.Add("Size", 80);
            lstFiles.Columns.Add("Modified Date", 130);
            lstFiles.Columns.Add("Hash Strength", 110);
            lstFiles.Columns.Add("Match Similarity", 110);
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = "Select CorelDRAW design folder to scan:";
                fbd.SelectedPath = txtFolder.Text;
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    txtFolder.Text = fbd.SelectedPath;
                }
            }
        }

        private async void btnScan_Click(object sender, EventArgs e)
        {
            if (btnScan.Text == "Cancel")
            {
                if (_cts != null)
                {
                    _cts.Cancel();
                    AppendLog("Canceling scan...");
                }
                return;
            }

            string scanPath = txtFolder.Text;
            if (!Directory.Exists(scanPath))
            {
                MessageBox.Show("Selected directory does not exist.", "Invalid Path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Reset UI
            _scannedFiles.Clear();
            _duplicateGroups.Clear();
            lstFiles.Items.Clear();
            lstFiles.Groups.Clear();
            lblStatus.Text = "Starting folder scan...";
            progressBar1.Value = 0;
            btnScan.Text = "Cancel";
            btnBrowse.Enabled = false;
            txtFolder.Enabled = false;
            chkRecursive.Enabled = false;
            numThreshold.Enabled = false;
            picPreview.Image = null;
            lblFileName.Text = "No file selected";
            lblFilePath.Text = "-";
            lblFileSize.Text = "-";
            lblFileDate.Text = "-";
            _selectedFileInfo = null;

            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            AppendLog("Starting scan in: " + scanPath);

            try
            {
                // Step 1: Enumerate file paths
                SearchOption searchOption = chkRecursive.Checked ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                string[] files = await Task.Run(() => Directory.GetFiles(scanPath, "*.cdr", searchOption), token);
                
                if (files.Length == 0)
                {
                    MessageBox.Show("No .CDR files found in the specified path.", "No Files Found", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    ResetUIState();
                    return;
                }

                AppendLog($"Found {files.Length} CorelDRAW files. Processing preview hashes...");
                progressBar1.Maximum = files.Length;

                // Step 2: Extract preview hashes asynchronously
                List<CdrFileInfo> scannedList = new List<CdrFileInfo>();
                int processedCount = 0;

                await Task.Run(() =>
                {
                    foreach (string filePath in files)
                    {
                        if (token.IsCancellationRequested)
                            break;

                        try
                        {
                            var info = new CdrFileInfo();
                            info.FullPath = filePath;
                            info.Name = Path.GetFileName(filePath);
                            
                            FileInfo fi = new FileInfo(filePath);
                            info.SizeBytes = fi.Length;
                            info.LastModified = fi.LastWriteTime;

                            // Extract preview image & compute hash
                            using (Bitmap preview = CdrPreviewExtractor.ExtractPreview(filePath))
                            {
                                if (preview != null)
                                {
                                    info.HasPreview = true;
                                    info.HashValue = ImageHasher.ComputeAverageHash(preview);
                                }
                                else
                                {
                                    info.HasPreview = false;
                                    info.HashValue = 0; // Unable to hash
                                }
                            }

                            scannedList.Add(info);
                        }
                        catch (Exception ex)
                        {
                            // Skip locked/unreadable files or report
                            System.Diagnostics.Debug.WriteLine(ex.Message);
                        }

                        processedCount++;
                        this.Invoke(new Action(() => {
                            progressBar1.Value = processedCount;
                            lblStatus.Text = $"Processed {processedCount}/{files.Length} files...";
                        }));
                    }
                }, token);

                if (token.IsCancellationRequested)
                {
                    AppendLog("Scan canceled by user.");
                    ResetUIState();
                    return;
                }

                _scannedFiles = scannedList;
                AppendLog("Calculating duplicate matches using Hamming distance...");

                // Step 3: Run hash clustering to find duplicates
                int threshold = (int)numThreshold.Value;
                await Task.Run(() => ComputeDuplicates(threshold), token);

                // Step 4: Populate results
                PopulateResultsToListView();

                AppendLog($"Scanning finished. Scanned {_scannedFiles.Count} files. Located {_duplicateGroups.Count} similar design groups.");
                lblStatus.Text = $"Completed: Found {_duplicateGroups.Count} duplicate groups.";
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occurred during scanning:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                AppendLog("Error: " + ex.Message);
            }
            finally
            {
                ResetUIState();
            }
        }

        private void ResetUIState()
        {
            btnScan.Text = "Scan Folder";
            btnBrowse.Enabled = true;
            txtFolder.Enabled = true;
            chkRecursive.Enabled = true;
            numThreshold.Enabled = true;
            if (_cts != null)
            {
                _cts.Dispose();
                _cts = null;
            }
        }

        private void ComputeDuplicates(int threshold)
        {
            // Simple clustering based on Hamming distance
            bool[] visited = new bool[_scannedFiles.Count];
            List<DuplicateGroup> groups = new List<DuplicateGroup>();

            for (int i = 0; i < _scannedFiles.Count; i++)
            {
                if (visited[i]) continue;
                CdrFileInfo file1 = _scannedFiles[i];
                if (!file1.HasPreview) continue; // Only compare files that contain valid previews

                List<CdrFileInfo> currentCluster = new List<CdrFileInfo>();
                
                for (int j = i + 1; j < _scannedFiles.Count; j++)
                {
                    if (visited[j]) continue;
                    CdrFileInfo file2 = _scannedFiles[j];
                    if (!file2.HasPreview) continue;

                    int distance = ImageHasher.CalculateHammingDistance(file1.HashValue, file2.HashValue);
                    if (distance <= threshold)
                    {
                        visited[j] = true;
                        file2.HammingDistanceMatch = distance;
                        currentCluster.Add(file2);
                    }
                }

                if (currentCluster.Count > 0)
                {
                    visited[i] = true;
                    file1.HammingDistanceMatch = 0; // Reference item
                    
                    var group = new DuplicateGroup();
                    group.ReferenceFile = file1;
                    group.MatchedFiles = currentCluster;
                    group.GroupName = $"Design Group: {file1.Name} ({100}% base match)";
                    groups.Add(group);
                }
            }

            _duplicateGroups = groups;
        }

        private void PopulateResultsToListView()
        {
            lstFiles.BeginUpdate();
            lstFiles.Items.Clear();
            lstFiles.Groups.Clear();

            int groupIndex = 1;
            foreach (var group in _duplicateGroups)
            {
                ListViewGroup listGroup = new ListViewGroup($"Group {groupIndex}: Similiar to '{group.ReferenceFile.Name}'");
                lstFiles.Groups.Add(listGroup);

                // Add Reference
                var refItem = CreateListViewItem(group.ReferenceFile, "Base Reference (0 distance)", listGroup);
                lstFiles.Items.Add(refItem);

                // Add Matches
                foreach (var match in group.MatchedFiles)
                {
                    int matchPercent = (int)((((64.0 - match.HammingDistanceMatch) / 64.0) * 100));
                    var matchItem = CreateListViewItem(match, $"Match distance: {match.HammingDistanceMatch} ({matchPercent}% similar)", listGroup);
                    lstFiles.Items.Add(matchItem);
                }

                groupIndex++;
            }

            lstFiles.EndUpdate();

            if (_duplicateGroups.Count == 0)
            {
                AppendLog("No duplicate designs or highly similar matches found with specified similarity threshold.");
            }
        }

        private ListViewItem CreateListViewItem(CdrFileInfo info, string relation, ListViewGroup lvg)
        {
            double sizeMb = (double)info.SizeBytes / (1024.0 * 1024.0);
            string displaySize = sizeMb >= 0.1 ? $"{sizeMb:F2} MB" : $"{((double)info.SizeBytes / 1024.0):F1} KB";

            ListViewItem item = new ListViewItem(new string[] {
                info.Name,
                Path.GetDirectoryName(info.FullPath),
                displaySize,
                info.LastModified.ToString("yyyy-MM-dd HH:mm:ss"),
                info.HashValue.ToString("X16"),
                relation
            });

            item.Tag = info;
            item.Group = lvg;
            return item;
        }

        private void lstFiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstFiles.SelectedItems.Count == 0)
                return;

            CdrFileInfo info = lstFiles.SelectedItems[0].Tag as CdrFileInfo;
            if (info == null)
                return;

            _selectedFileInfo = info;
            lblFileName.Text = info.Name;
            lblFilePath.Text = info.FullPath;
            
            double sizeMb = (double)info.SizeBytes / (1024.0 * 1024.0);
            lblFileSize.Text = sizeMb >= 0.1 ? $"{sizeMb:F2} MB ({info.SizeBytes:N0} bytes)" : $"{((double)info.SizeBytes / 1024.0):F1} KB ({info.SizeBytes:N0} bytes)";
            lblFileDate.Text = info.LastModified.ToString("yyyy-MM-dd HH:mm:ss");

            // Update Image Preview
            if (picPreview.Image != null)
            {
                picPreview.Image.Dispose();
                picPreview.Image = null;
            }

            if (info.HasPreview)
            {
                try
                {
                    Bitmap previewImg = CdrPreviewExtractor.ExtractPreview(info.FullPath);
                    if (previewImg != null)
                    {
                        picPreview.Image = previewImg;
                        picPreview.SizeMode = PictureBoxSizeMode.Zoom;
                    }
                    else
                    {
                        DrawPlaceholder("No Embedded Preview");
                    }
                }
                catch
                {
                    DrawPlaceholder("Error Loading Preview");
                }
            }
            else
            {
                DrawPlaceholder("Preview Not Found (Older CDR version)");
            }
        }

        private void DrawPlaceholder(string text)
        {
            Bitmap bmp = new Bitmap(240, 240);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Gainsboro);
                using (Font f = new Font("Segoe UI", 9.5f, FontStyle.Regular))
                using (StringFormat sf = new StringFormat())
                {
                    sf.Alignment = StringAlignment.Center;
                    sf.LineAlignment = StringAlignment.Center;
                    g.DrawString(text, f, Brushes.DimGray, new RectangleF(0, 0, 240, 240), sf);
                }
                // Draw decorative outline
                using (Pen p = new Pen(Color.DarkGray, 2f))
                {
                    p.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash;
                    g.DrawRectangle(p, 10, 10, 220, 220);
                }
            }
            picPreview.Image = bmp;
        }

        private void AppendLog(string message)
        {
            txtLog.Invoke(new Action(() => {
                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                txtLog.SelectionStart = txtLog.TextLength;
                txtLog.ScrollToCaret();
            }));
        }

        private void btnCopy_Click(object sender, EventArgs e)
        {
            if (_selectedFileInfo == null)
            {
                MessageBox.Show("Please select a file from the list first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Clipboard.SetText(_selectedFileInfo.FullPath);
                MessageBox.Show("File path copied to clipboard!", "Copied", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to copy path: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnOpenFolder_Click(object sender, EventArgs e)
        {
            if (_selectedFileInfo == null)
            {
                MessageBox.Show("Please select a file from the list first.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(_selectedFileInfo.FullPath);
                if (Directory.Exists(directory))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_selectedFileInfo.FullPath}\"");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not open folder: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void btnDelete_Click(object sender, EventArgs e)
        {
            if (_selectedFileInfo == null)
            {
                MessageBox.Show("Please select a file to delete.", "No Selection", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var result = MessageBox.Show(
                $"Are you sure you want to permanently delete this CorelDRAW file?\n\nFile Name: {_selectedFileInfo.Name}\nPath: {_selectedFileInfo.FullPath}\n\nWARNING: This action cannot be undone!", 
                "Confirm File Deletion", 
                MessageBoxButtons.YesNo, 
                MessageBoxIcon.Warning
            );

            if (result == DialogResult.Yes)
            {
                try
                {
                    File.Delete(_selectedFileInfo.FullPath);
                    AppendLog("Successfully deleted: " + _selectedFileInfo.FullPath);
                    MessageBox.Show("File deleted successfully.", "Deleted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    
                    // Remove from list view and local collections
                    RemoveFileFromLocalCollections(_selectedFileInfo);
                    PopulateResultsToListView();

                    picPreview.Image = null;
                    lblFileName.Text = "No file selected";
                    lblFilePath.Text = "-";
                    lblFileSize.Text = "-";
                    lblFileDate.Text = "-";
                    _selectedFileInfo = null;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to delete file. Is it open in CorelDRAW or locked?\n\nException: " + ex.Message, "Deletion Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RemoveFileFromLocalCollections(CdrFileInfo target)
        {
            _scannedFiles.Remove(target);
            foreach (var group in _duplicateGroups)
            {
                if (group.ReferenceFile == target)
                {
                    if (group.MatchedFiles.Count > 0)
                    {
                        // Promote first match to reference
                        CdrFileInfo nextRef = group.MatchedFiles[0];
                        group.MatchedFiles.RemoveAt(0);
                        nextRef.HammingDistanceMatch = 0;
                        group.ReferenceFile = nextRef;
                        group.GroupName = $"Design Group: {nextRef.Name} (100% base match)";
                    }
                    else
                    {
                        _duplicateGroups.Remove(group);
                        break;
                    }
                }
                else if (group.MatchedFiles.Contains(target))
                {
                    group.MatchedFiles.Remove(target);
                }
            }

            // Cleanup empty groups
            _duplicateGroups.RemoveAll(g => g.MatchedFiles.Count == 0);
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            if (_duplicateGroups.Count == 0)
            {
                MessageBox.Show("No similarities found to export. Run a scan first.", "No Match Data", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "CSV Reports (*.csv)|*.csv|Text Files (*.txt)|*.txt";
                sfd.Title = "Export Duplicates Report";
                sfd.FileName = "CorelDraw_Duplicates_Report.csv";

                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.OpenFile()))
                        {
                            sw.WriteLine("Group,Type,FileName,FilePath,SizeBytes,LastWriteTime,ImageHashValue,HammingDistance");
                            int idx = 1;
                            foreach (var group in _duplicateGroups)
                            {
                                sw.WriteLine($"Group {idx},Reference,{EscapeCsv(group.ReferenceFile.Name)},{EscapeCsv(group.ReferenceFile.FullPath)},{group.ReferenceFile.SizeBytes},{group.ReferenceFile.LastModified:O},{group.ReferenceFile.HashValue:X16},0");
                                foreach (var match in group.MatchedFiles)
                                {
                                    sw.WriteLine($"Group {idx},Match,{EscapeCsv(match.Name)},{EscapeCsv(match.FullPath)},{match.SizeBytes},{match.LastModified:O},{match.HashValue:X16},{match.HammingDistanceMatch}");
                                }
                                idx++;
                            }
                        }
                        AppendLog("Report exported successfully to: " + sfd.FileName);
                        MessageBox.Show("Report exported successfully!", "Exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Export failed: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private string EscapeCsv(string val)
        {
            if (string.IsNullOrEmpty(val)) return "";
            if (val.Contains(",") || val.Contains("\"") || val.Contains("\n") || val.Contains("\r"))
            {
                return "\"" + val.Replace("\"", "\"\"") + "\"";
            }
            return val;
        }
    }

    public class CdrFileInfo
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public long SizeBytes { get; set; }
        public DateTime LastModified { get; set; }
        public ulong HashValue { get; set; }
        public bool HasPreview { get; set; }
        public int HammingDistanceMatch { get; set; }
    }

    public class DuplicateGroup
    {
        public string GroupName { get; set; }
        public CdrFileInfo ReferenceFile { get; set; }
        public List<CdrFileInfo> MatchedFiles { get; set; }
    }
}
