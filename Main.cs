using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace WumboLauncher
{
    public partial class Main : Form
    {
        public Main()
        {
            InitializeComponent();
        }

        /*-----------+
         | VARIABLES |
         +-----------*/

        // Previous width of the list
        int prevWidth;
        // Metadata fields to be retrieved alongside their proper names
        string[,] metadataFields =
        {
            { "Title", "title" },
            { "Alternate Titles", "alternateTitles" },
            { "Series", "series" },
            { "Developer", "developer" },
            { "Publisher", "publisher" },
            { "Source", "source" },
            { "Release Date", "releaseDate" },
            { "Platform", "platform" },
            { "Version", "version" },
            { "Tags", "tagsStr" },
            { "Language", "language" },
            { "Play Mode", "playMode" },
            { "Status", "status" },
            { "Notes", "notes" },
            { "Original Description", "originalDescription" },
            { "", "activeDataOnDisk" }
        };
        // Titles to be displayed above each column
        string[] columnHeaders = { "Title", "Developer", "Publisher" };
        // Calculated column widths before conversion to int
        List<double> columnWidths = new();
        // Names of tags to be filtered
        List<string> filteredTags = new();
        // Characters that cause issues in searches ( % _ ' " )
        string unsafeChars = "%_\'\""; 
        // Query fragments used to fetch entries
        string queryLibrary = "arcade";
        int queryOrderBy = 0;
        int queryDirection = 1;
        string querySearch = "";
        // Template for list items
        class QueryItem
        {
            public string Title { get; set; } = "";
            public string Developer { get; set; } = "";
            public string Publisher { get; set; } = "";
            public int Index { get; set; } = -1;
            public string TagsStr { get; set; } = "";
        }
        // Cache of all items to be displayed in list
        List<QueryItem> queryCache = new();
        // Lock for the above.
        readonly object queryCacheLock = new();
        // A ManualResetEvent, to signal when a new item is ready for reading.
        ManualResetEventSlim queryCacheWH = new(true);
        // Check if column width has been changed manually
        bool columnChanged = false;
        // Check if images have been loaded
        bool logoLoaded = false;
        bool screenshotLoaded = false;
        readonly int unfilteredPageSize = 500;

        /*--------+
         | EVENTS |
         +--------*/

        private void Main_load(object sender, EventArgs e)
        {
            // Create configuration file if one doesn't exist
            if (File.Exists("config.json") && File.ReadAllText("config.json").Length > 0)
                Config.Read();
            }
            else
            {
                Config.Write();
            }

            // Why Visual Studio doesn't let me do this the regular way, I don't know
            SearchBox.AutoSize = false;
            SearchBox.Height = 20;

            // Start this in a new thread.
            new Thread(InitializeDatabase).Start();
            //InitializeDatabase();
        }

        private void Main_resize(object sender, EventArgs e)
        {
            // Scale column widths to list width
            if (columnChanged)
            {
                ScaleColumns();
            }
            else
            {
                AdjustColumns();
            }

            prevWidth = ArchiveList.ClientSize.Width;

            // Resize metadata textbox to new height
            ArchiveInfoData.Height = GetInfoHeight();
        }

        // Initialize list when Archive tab is accessed for the first time
        // Update column widths if window is resized while in a different tab
        private void TabControl_tabChanged(object sender, EventArgs e)
        {
            if (((TabControl)sender).SelectedIndex == 1)
            {
                if (Config.NeedsRefresh)
                {
                    InitializeDatabase();
                }
                else if (columnChanged)
                {
                    ScaleColumns();
                }
                else
                {
                    AdjustColumns();
                }
            }
        }

        // Execute search if enter is pressed
        private void SearchBox_keyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                ExecuteSearchQuery();
                e.SuppressKeyPress = true;
            }
        }

        // Execute search if Search button is clicked
        private void SearchButton_click(object sender, EventArgs e) { ExecuteSearchQuery(); }

        // Display setttings menu when Settings button is clicked
        private void SettingsButton_click(object sender, EventArgs e) { OpenSettings(); }

        // Reload database when 
        private void SettingsMenu_formClosed(object? sender, FormClosedEventArgs e)
        {
            Config.Read();

            if (TabControl.SelectedIndex == 1 && Config.NeedsRefresh)
            {
                InitializeDatabase();
            }
        }

        // Display items on list when fetched
        private void ArchiveList_retrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            // While we don't yet have the item in question,
            while (GetQueryCacheSize() <= e.ItemIndex)
            {
                // Wait for a signal that a new item has been added.
                queryCacheWH.Wait();
                // Reset the signal so that we won't spin.
                queryCacheWH.Reset();
            }
            // A temporary variable, to hold the QueryItem in question.
            QueryItem temp;
            // While locking to ensure coherence, grab the QueryItem.
            lock (queryCacheLock)
            {
                temp = queryCache[e.ItemIndex];
            }
            // Construct the entry out of it, and set the entry.
            ListViewItem entry = new(temp.Title);
            entry.SubItems.Add(temp.Developer);
            entry.SubItems.Add(temp.Publisher);

            e.Item = entry;
        }
        /// <summary>
        /// Essentially just a locking version of queryCache.Count.
        /// </summary>
        /// <returns>queryCache.Count</returns>
        private int GetQueryCacheSize()
        {
            lock (queryCacheLock)
            {
                return queryCache.Count;
            }
        }

        // Display information about selected entry in info panel
        private void ArchiveList_itemSelect(object sender, EventArgs e)
        {
            ListView.SelectedIndexCollection selectedIndices = ((ListView)sender).SelectedIndices;

            /*
             *  I need to figure out how to clear the info panel only when an item is deselected
             *  Otherwise, the list flickers every time a new item is selected
             *  
             *  SelectedIndexChanged fires twice, returning empty SelectedIndices the first time
             *  SelectedItemsChanged fires once but doesn't fire when item is deselected
             */
            if (selectedIndices.Count == 0)
            {
                ClearInfoPanel();
                return;
            }

            int entryIndex = queryCache[selectedIndices[0]].Index;

            List<string> metadataOutput = new(metadataFields.GetLength(0));

            for (int i = 0; i < metadataFields.GetLength(0); i++)
            {
                metadataOutput.Add(
                    DatabaseQuery(metadataFields[i, 1], entryIndex)[0]
                );
            }

            // Header

            ArchiveInfoTitle.Text = metadataOutput[0];

            if (metadataOutput[3] != "")
            {
                ArchiveInfoDeveloper.Text = $"by {metadataOutput[3]}";
            }
            else
            {
                ArchiveInfoDeveloper.Text = "by unknown developer";
            }

            ArchiveInfoData.Height = GetInfoHeight();

            // Metadata

            string entryData = @"{\rtf1 ";

            for (int i = 1; i < metadataOutput.Count - 1; i++)
                if (metadataOutput[i] != "")
                {
                    if (metadataFields[i, 1] == "notes" || metadataFields[i, 1] == "originalDescription")
                    {
                        entryData += $"\\line\\b {metadataFields[i, 0]}:\\line\\b0 {ToUnicode(metadataOutput[i])}\\line";
                    }
                    else
                    {
                        entryData += $"\\b {metadataFields[i, 0]}: \\b0 {ToUnicode(metadataOutput[i])}\\line";
                    }
                }
            }

            entryData += "}";

            ArchiveInfoData.Rtf = entryData;

            // Images

            if (!ArchiveImagesContainer.Visible)
            {
                ArchiveImagesContainer.Visible = true;
            }

            string entryId = DatabaseQuery("id", entryIndex)[0];
            foreach (string folder in new string[] { "Logos", "Screenshots" })
            {
                string[] imageTree = { entryId.Substring(0, 2), entryId.Substring(2, 2) };
                string imagePath = $"\\Data\\Images\\{folder}\\{imageTree[0]}\\{imageTree[1]}\\{entryId}.png";

                if (File.Exists(Config.FlashpointPath + imagePath))
                {
                    if (folder == "Logos")
                    {
                        ArchiveImagesLogo.Image = Image.FromFile(Config.FlashpointPath + imagePath);
                        logoLoaded = true;
                    }
                    else if (folder == "Screenshots")
                    {
                        ArchiveImagesScreenshot.Image = Image.FromFile(Config.FlashpointPath + imagePath);
                        screenshotLoaded = true;
                    }
                }
                else
                {
                    if (folder == "Logos")
                        ArchiveImagesLogo.ImageLocation = Config.FlashpointServer + imagePath;
                    else if (folder == "Screenshots")
                        ArchiveImagesScreenshot.ImageLocation = Config.FlashpointServer + imagePath;
                }
            }

            // Footer

            if (metadataOutput[15] == "1")
                PlayButton.Text = "Play";
            else
                PlayButton.Text = "Play (Legacy)";

            PlayButton.Visible = true;
        }

        // Launch selected entry
        private void ArchiveList_itemAccess(object sender, EventArgs e)
        {
            int entryIndex = queryCache[ArchiveList.SelectedIndices[0]].Index;

            if (File.Exists(Config.CLIFpPath))
            {
                LaunchEntry.StartInfo.FileName = Config.CLIFpPath;
                LaunchEntry.StartInfo.Arguments = $"play -i {DatabaseQuery("id", entryIndex)[0]}";
                LaunchEntry.Start();
            }
            else
            {
                MessageBox.Show("CLIFp not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Update columnWidths in case column is changed manually
        private void ArchiveList_columnChanged(object sender, ColumnWidthChangedEventArgs e)
        {
            if (ArchiveList.ClientSize.Width == prevWidth)
            {
                columnWidths[e.ColumnIndex] = ArchiveList.Columns[e.ColumnIndex].Width;
                columnChanged = true;
            }
        }

        // Automatically sort clicked column
        private void ArchiveList_columnClick(object sender, ColumnClickEventArgs e)
        {
            // Set column to sort by and reverse direction
            queryOrderBy = e.Column;
            queryDirection *= -1;

            // Remove any indicators that might be visible
            for (int i = 0; i < columnHeaders.Length; i++)
            {
                if (ArchiveList.Columns[i].Text != columnHeaders[i])
                {
                    ArchiveList.Columns[i].Text = columnHeaders[i];
                }
            }

            // Add a new arrow indicator to column header
            string arrow = char.ConvertFromUtf32(0x2192 + queryDirection);
            ArchiveList.Columns[queryOrderBy].Text = $"{columnHeaders[queryOrderBy]}  {arrow}";

            SortColumns();

            ArchiveList.VirtualListSize = 0;
            ArchiveList.VirtualListSize = Convert.ToInt32(queryCache.Count);
        }

        // Preserve arrow cursor when hovering over selected item
        private void ArchiveList_mouseMove(object sender, MouseEventArgs e) { base.Cursor = Cursors.Arrow; }

        // Reroute Adjust Columns button to its appropriate function
        private void AdjustColumnsButton_click(object sender, EventArgs e) { AdjustColumns(); }

        // Change library when left panel radio is changed
        private void ArchiveRadio_checkedChanged(object sender, EventArgs e)
        {
            RadioButton checkedRadio = (RadioButton)sender;
            string queryLibraryOld = queryLibrary;
            if (checkedRadio.Checked)
            {
                if (checkedRadio.Name == "ArchiveRadioGames")
                {
                    queryLibrary = "arcade";
                }
                else if (checkedRadio.Name == "ArchiveRadioAnimations")
                {
                    queryLibrary = "theatre";
                }
                if (queryLibrary != queryLibraryOld)
                {
                    RefreshDatabase();
                }
            }
        }

        private void ArchiveImages_loadCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            if (((PictureBox)sender).Name == "ArchiveImagesLogo")
            {
                logoLoaded = true;
            }
            else if (((PictureBox)sender).Name == "ArchiveImagesScreenshot")
            {
                screenshotLoaded = true;
            }
        }

        // Display picture viwwer 
        private void ArchiveImages_click(object sender, EventArgs e)
        {
            string pictureName = ((PictureBox)sender).Name;

            if ((pictureName == "ArchiveImagesLogo" && logoLoaded) ||
                (pictureName == "ArchiveImagesScreenshot" && screenshotLoaded))
            {
                Form pictureViewer = new();
                pictureViewer.Size = new Size(640, 480);
                pictureViewer.Controls.Add(new PictureBox()
                {
                    Name = "PictureContainer",
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Image = pictureName == "ArchiveImagesLogo" ? ArchiveImagesLogo.Image : ArchiveImagesScreenshot.Image,
                });
                pictureViewer.Show();
            }
        }

        /*-----------+
         | FUNCTIONS |
         +-----------*/

        // Check if database is valid and load if so
        // Adapted from https://stackoverflow.com/a/70291358
        private void InitializeDatabase()
        {
            if (Config.NeedsRefresh)
            {
                Config.NeedsRefresh = false;
            }

            string databasePath = Config.FlashpointPath + @"\Data\flashpoint.sqlite";
            byte[] header = new byte[16];

            if (File.Exists(databasePath))
            {
                using (FileStream fileStream = new(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fileStream.Read(header, 0, 16);
                }

                if (Encoding.ASCII.GetString(header).Contains("SQLite format"))
                {
                    // Add columns to list and get width for later
                    if (ArchiveList.Columns.Count != columnHeaders.Length)
                    {
                        for (int i = 0; i < columnHeaders.Length; i++)
                        {
                            ArchiveList.Columns.Add(columnHeaders[i]);
                            columnWidths.Add(ArchiveList.Columns[i].Width);
                        }

                        prevWidth = ArchiveList.ClientSize.Width;
                    }

                    LoadFilteredTags();
                    RefreshDatabase();

                    if (queryLibrary == "arcade")
                    {
                        ArchiveRadioGames.Checked = true;
                    }
                    else if (queryLibrary == "theatre")
                    {
                        ArchiveRadioAnimations.Checked = true;
                    }

                    RefreshDatabase(library: queryLibrary);

                    return;
                }
            }

            ArchiveList.VirtualListSize = 0;
            ClearInfoPanel();

            MessageBox.Show("Database is either corrupted or missing!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            TabControl.SelectTab(0);
            OpenSettings();

            Config.NeedsRefresh = true;
        }

        // Generate new cache and refresh list
        private void RefreshDatabase(string searchFor = "", string library = "arcade")
        {
            ClearInfoPanel();
            lock (queryCacheLock)
            {
                queryCache.Clear();
            }

            bool atEnd = false;
            List<QueryItem> temp;
            // This is the lowest string, in SQL's mind.
            string lastTitle = "";
            int i, listlen = 0;
            using (SqliteConnection connection = new($"Data Source={Config.Data[0]}\\Data\\flashpoint.sqlite"))
            {
                connection.Open();
                while (!atEnd)
                {
                    temp = DatabaseQueryBlock(connection, library, searchFor, lastTitle, unfilteredPageSize);
                    for (i = 0; i < temp.Count; i++)
                    {
                        if (!filteredTags.Intersect(temp[i].TagsStr.Split("; ")).Any()) {
                            lock (queryCacheLock)
                            {
                                queryCache.Add(temp[i]);
                                listlen = queryCache.Count;
                                queryCache[listlen-1].Index = listlen - 1;
                            }
                        }

                    }
                    lastTitle = temp[temp.Count-1].Title;
                    atEnd = (temp.Count < unfilteredPageSize);
                }
                connection.Close();
            }
            lock (queryCacheLock)
            {
                SafelySetVirtualListSize(0);
                SafelySetVirtualListSize(queryCache.Count);
                // Display entry count in bottom right corner
                SafelySetEntryCountText($"Displaying {queryCache.Count} entr" + (queryCache.Count == 1 ? "y" : "ies") + ".");
            }


            // Sort new queryCache.
            // Nope, not doing this.
            //SortColumns();

            // Prevent column widths from breaking out of list
            if (columnChanged)
            {
                ScaleColumns();
                AdjustColumns();
            }
            else
            {
                AdjustColumns();
            }
        }

        public void SafelySetVirtualListSize(int newsize)
        {
            if (ArchiveList.InvokeRequired)
            {
                ArchiveList.Invoke((Action)delegate { SafelySetVirtualListSize(newsize); });
            }
            else
            {
                ArchiveList.VirtualListSize = newsize;
            }
        }
        public void SafelySetEntryCountText(string text)
        {
            if (EntryCountLabel.InvokeRequired)
            {
                EntryCountLabel.Invoke((Action)delegate { SafelySetEntryCountText(text); });
            }
            else
            {
                EntryCountLabel.Text = text;
            }
        }

        private void LoadFilteredTags()
        {
            filteredTags.Clear();

            if (File.Exists("filters.json"))
            {
                using (StreamReader jsonStream = new("filters.json"))
                {
                    dynamic? filterArray = JsonConvert.DeserializeObject(jsonStream.ReadToEnd());

                    foreach (var item in filterArray)
                        if (item.filtered == true)
                            foreach (var tag in item.tags)
                                filteredTags.Add(tag.ToString());
                }
            }
            else
                MessageBox.Show(
                    "filters.json was not found, and as a result the archive will be unfiltered. Use at your own risk.",
                    "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning
                );
        }

        // Return items from the Flashpoint database
        private List<string> DatabaseQuery(string column, int offset = -1)
        {
            SqliteConnection connection = new($"Data Source={Config.FlashpointPath}\\Data\\flashpoint.sqlite");
            connection.Open();

            SqliteCommand command = new(
                $"SELECT {column} FROM game " +
                $"WHERE library = '{queryLibrary}' " +
                (querySearch != "" ? $"AND title LIKE '%{querySearch}%' " : "") +
                $"ORDER BY title {(offset != -1 ? $"LIMIT {offset}, 1 " : "")}"
            , connection);

            List<string> data = new();

            using (SqliteDataReader dataReader = command.ExecuteReader())
            {
                while (dataReader.Read())
                {
                    data.Add(dataReader.IsDBNull(0) ? "" : dataReader.GetString(0));
                }
            }

            connection.Close();

            return data;
        }
        /// <summary>
        /// Reads a block of QueryItems from a dbConn using keyset pagination so that we don't slow down on later blocks.
        /// </summary>
        /// <param name="dbConn">The database connection to use.</param>
        /// <param name="library">The library (either "arcade" or "theatre") that the entries are from. Allows us to use a built-in index.</param>
        /// <param name="search">The string to search for in the title.</param>
        /// <param name="titleGreaterThan">The final title from the last unfiltered block.</param>
        /// <param name="blockSize">The size of the data block to request.</param>
        /// <returns>The block of data, with the Index field uninitialized in each QueryItem.</returns>
        private static List<QueryItem> DatabaseQueryBlock(SqliteConnection dbConn, string library, string search, string titleGreaterThan, int blockSize)
        {
            // I purposefully replace all instances of "'" in the strings with "''", to escape them.
            SqliteCommand command = new(
                // Some of these don't need to be $-strings. Keeping them that way to look pretty though.
                // Note that all of these, if they're non-empty, end in a space. This is intentional.
                $"SELECT title,developer,publisher,tagsStr FROM game " +
                // Apparently this uses a built-in index?
                $"WHERE library is '{library.Replace("'", "''")}' " +
                // This is the keyset pagination trick that Seirade mentioned.
                $"AND title > '{titleGreaterThan.Replace("'", "''")}' " +
                // If we're searching, include the search string. I think this is more expensive than the above command, so it goes second.
                (search != "" ? $"AND title LIKE '%{search.Replace("'", "''")}%' " : "") +
                // This will ensure that we don't mix up our ordering - it's critical that we keep it straight.
                $"ORDER BY title " +
                // Read only a block. We don't want the whole thing.
                // Yeah, the trailing space isn't needed, but at least it won't trip up people who are extending this.
                $"LIMIT {blockSize} ",
                // And our lovely database connection.
                dbConn);
            // Make a new list to hold our results. TODO: memory leak?
            List<QueryItem> results = new();
            // This will ensure that everything is cleaned up properly.
            using (SqliteDataReader dataReader = command.ExecuteReader())
            {
                // Read the rows one by one. Bail out when we have no more rows.
                while (dataReader.Read())
                {
                    // Add the row to the results list.
                    results.Add(new QueryItem
                    {
                        // Create a new QueryItem from the columns of this row.
                        Title = dataReader.IsDBNull(0) ? "" : dataReader.GetString(0),
                        Developer = dataReader.IsDBNull(1) ? "" : dataReader.GetString(1),
                        Publisher = dataReader.IsDBNull(2) ? "" : dataReader.GetString(2),
                        TagsStr = dataReader.IsDBNull(3) ? "" : dataReader.GetString(3)
                        // Note: we don't initialize the Index.
                    });
                }
            }
            // Return the results array.
            return results;
        }

        private void ExecuteSearchQuery()
        {
            StringBuilder safeQuery = new();

            foreach (char inputChar in SearchBox.Text.ToLower())
            {
                if (unsafeChars.Contains(inputChar))
                {
                    safeQuery.Append('_');
                }
                else
                {
                    safeQuery.Append(inputChar);
                }
            }

            querySearch = safeQuery.ToString();
            RefreshDatabase();

            TabControl.SelectTab(1);
        }

        private void OpenSettings()
        {
            Settings SettingsMenu = new Settings();
            SettingsMenu.FormClosed += new FormClosedEventHandler(SettingsMenu_formClosed);

            SettingsMenu.ShowDialog();
        }

        // Resize columns proportional to new list size
        private void ScaleColumns()
        {
            if (ArchiveList.InvokeRequired)
            {
                ArchiveList.Invoke((Action)delegate { ScaleColumns(); });
            }
            else
            {
                for (int i = 0; i < ArchiveList.Columns.Count; i++)
                {
                    columnWidths[i] *= (double)ArchiveList.ClientSize.Width / prevWidth;
                    ArchiveList.Columns[i].Width = (int)columnWidths[i];
                }
            }
        }

        // Resize columns to fill list width
        private void AdjustColumns()
        {
            if (ArchiveList.InvokeRequired)
            {
                ArchiveList.Invoke(delegate { AdjustColumns(); });
            } else
            {
                int divisor = ArchiveList.Columns.Count + 1;

                ArchiveList.BeginUpdate();
                for (int i = 0; i < ArchiveList.Columns.Count; i++)
                {
                    columnWidths[i] = (ArchiveList.ClientSize.Width / divisor) * (i == 0 ? 2 : 1);
                    ArchiveList.Columns[i].Width = (int)columnWidths[i];
                }
                ArchiveList.EndUpdate();

                if (columnChanged)
                {
                    columnChanged = false;
                }
            }
        }

        // Sort items by a specific column
        private void SortColumns()
        {
            switch (columnHeaders[queryOrderBy])
            {
                case "Title":
                    queryCache = (
                        queryDirection == 1
                        ? queryCache.OrderBy(i => i.Title)
                        : queryCache.OrderByDescending(i => i.Title)
                    ).ToList();
                    break;

                case "Developer":
                    queryCache = (
                        queryDirection == 1
                        ? queryCache.OrderBy(i => i.Developer)
                        : queryCache.OrderByDescending(i => i.Developer)
                    ).ToList();
                    break;

                case "Publisher":
                    queryCache = (
                        queryDirection == 1
                        ? queryCache.OrderBy(i => i.Publisher)
                        : queryCache.OrderByDescending(i => i.Publisher)
                    ).ToList();
                    break;
            }
        }

        // Leave the right amount of room for metadata text box
        private int GetInfoHeight()
        {
            int desiredHeight = ArchiveInfoContainer.Height - 14;

            foreach (Control control in ArchiveInfoContainer.Controls)
            {
                if (control.Name != "ArchiveInfoData")
                {
                    desiredHeight -= control.Height;
                }
            }

            return desiredHeight;
        }

        private void ClearInfoPanel()
        {
            // If we're not on the main thread,
            if (ArchiveInfoData.InvokeRequired)
            {
                // Run on the main thread.
                ArchiveInfoData.Invoke((Action)delegate { ClearInfoPanel(); });
            }
            else
            {
                ArchiveInfoTitle.Text = "";
                ArchiveInfoDeveloper.Text = "";
                ArchiveInfoData.Rtf = "";
                ArchiveInfoData.Height = 0;
                ArchiveImagesContainer.Visible = false;
                ArchiveImagesLogo.Image = null;
                ArchiveImagesScreenshot.Image = null;
                logoLoaded = false;
                screenshotLoaded = false;
                PlayButton.Visible = false;
            }
        }

        // Make strings suitable for RTF text box
        // Adapted from https://stackoverflow.com/a/30363185
        static string ToUnicode(string data)
        {
            StringBuilder escapedData = new();

            foreach (char c in data)
            {
                if (c == '\\' || c == '{' || c == '}')
                {
                    escapedData.Append(@"\" + c);
                }
                else if (c <= 0x7f)
                {
                    escapedData.Append(c);
                }
                else
                {
                    escapedData.Append(@"\u" + Convert.ToUInt32(c) + "?");
                }
            }

            return escapedData.ToString().Replace("\n", @"\line ");
        }        
    }
}