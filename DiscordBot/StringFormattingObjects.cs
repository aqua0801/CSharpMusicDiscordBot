using System.Text;


namespace StringFormatting
{

    internal static class StringFormattingAlgorithm
    {
        public static float ToFixedWidth(string text)
        {
            float sum = 0f;
            foreach (char c in text)
            {
                if ((c >= 0x4E00 && c <= 0x9FFF) ||       
                    (c >= 0x3400 && c <= 0x4DBF) ||       
                    (c >= 0x20000 && c <= 0x2A6DF) ||     
                    (c >= 0x3040 && c <= 0x309F) ||      
                    (c >= 0x30A0 && c <= 0x30FF) ||       
                    (c >= 0x31F0 && c <= 0x31FF) ||      
                    (c >= 0xAC00 && c <= 0xD7AF) ||     
                    (c >= 0x1100 && c <= 0x11FF))       
                {
                    sum += 2f;  
                }
      
                else if (char.IsLetter(c))
                {
                    sum += 1f;  
                }
                // 判斷是否為數字（半角）
                else if (char.IsDigit(c))
                {
                    sum += 1f;  
                }
                // 處理一些常見符號
                else if (c == '.' || "~-=.? \u00A0".Contains(c))
                {
                    sum += 1f;  
                }
                else
                {
                    sum += 1f;  
                }
            }
            return sum;
        }


        public static string PadToFixedWidth(string text, string padding, float width, StringFormatter.PadAlign align)
        {
            float currentWidth = ToFixedWidth(text);
            if (currentWidth >= width) return text;
            int paddingCount = (int)Math.Ceiling((width - currentWidth) / ToFixedWidth(padding));
            if (align == StringFormatter.PadAlign.Left)
                return string.Concat(Enumerable.Repeat(padding, paddingCount)) + text;
            else if (align == StringFormatter.PadAlign.Right)
                return text + string.Concat(Enumerable.Repeat(padding, paddingCount));
            else
            {
                int leftPad = paddingCount / 2;
                int rightPad = paddingCount - leftPad;
                return string.Concat(Enumerable.Repeat(padding, leftPad)) + text + string.Concat(Enumerable.Repeat(padding, rightPad));
            }
        }
    }


    public class StringFormatter : IDisposable
    {
        public enum PadAlign
        {
            Left = 0,
            Center = 1,
            Right = 2
        }

        public enum Separator
        {
            Major, Minor
        }

        private const string PadString = " ";

        private List<string> StringTemps;

        private StringBuilder FormatBuilder;

        private List<float> MaxColumnWidths;
        /// <summary>
        /// Width Calculating Algorithm
        /// Param : TextToBeCalculated
        /// Return : Width
        /// </summary>
        private Func<string, float> CalculateWidthAlgorithm;
        /// <summary>
        /// Padding algorithm
        /// Param : TextToBePadded , PaddingString , TargetWidth
        /// Return : Padded string 
        /// </summary>
        private Func<string, string, float, string> PadStringAlgorithm;

        private Dictionary<int, Separator> SeparatorMap;

        public int PaddingSpace = 2;

        public StringFormatter(PadAlign Align, int Capacity)
        {
            this.StringTemps = new List<string>(Capacity);
            this.FormatBuilder = new StringBuilder(Capacity);
            this.MaxColumnWidths = new List<float>(Capacity);
            this.SeparatorMap = new Dictionary<int, Separator>();

               
            this.CalculateWidthAlgorithm = (t) => StringFormattingAlgorithm.ToFixedWidth(t) + this.PaddingSpace;
            this.PadStringAlgorithm = (t, p, w) => StringFormattingAlgorithm.PadToFixedWidth(t, p, w, Align);
                
        }


        /// <summary>
        /// Clear object (not Dispose , reuseable , but not recommended as this class is intended to be short lifecycle , disposed when finished)
        /// </summary>
        public void Clear()
        {
            this.StringTemps.Clear();
            this.FormatBuilder.Clear();
        }

        /// <summary>
        /// Insert separator to index(end of the string temp by default) (not pre-newlining , instead , newlined after separator)
        /// </summary>
        public void AddSeparator(StringFormatter.Separator sep, int? index = null)
        {
            if (index == null)
            {
                this.SeparatorMap.Add(this.StringTemps.Count, sep);
                this.StringTemps.Add("");
            }
            else
            {
                this.SeparatorMap.Add(index.Value, sep);
                this.StringTemps.Insert(index.Value, "");
            }
        }

        /// <summary>
        /// Get current string temp count.
        /// </summary>
        public int GetStringTempsCount() => this.StringTemps.Count;

        /// <summary>
        /// Add string (Add Width list if ColumnIndex exceeds , therefore Index is required to be asc. if Random Access is required , modify this method !)
        /// </summary>
        public void AddStringTemps(string TextToAdd, int ColumnIndex)
        {
            float Width = this.CalculateWidthAlgorithm(TextToAdd);
            if (ColumnIndex >= this.MaxColumnWidths.Count)
                this.MaxColumnWidths.Add(Width);
            else
            {
                if (Width > this.MaxColumnWidths[ColumnIndex])
                    this.MaxColumnWidths[ColumnIndex] = Width;
            }
            this.StringTemps.Add(TextToAdd);
        }

        /// <summary>
        /// Add string without considering width.
        /// </summary>
        public void AddStringTemps(string TextToAdd)
        {
            this.StringTemps.Add(TextToAdd);
        }

        /// <summary>
        /// Newline , equal to .AddString(Environment.NewLine) but adds more readability.
        /// </summary>
        public void NewLine()
        {
            this.StringTemps.Add(Environment.NewLine);
        }

        /// <summary>
        /// Create respective separator string.
        /// </summary>
        public string GenerateSeparators(Separator sep)
        {
            StringBuilder SepBuilder = new StringBuilder(1000);
            if (sep == Separator.Major)
                foreach (var width in this.MaxColumnWidths)
                    SepBuilder.Append(this.PadStringAlgorithm("=", "=", width));
            else if (sep == Separator.Minor)
                foreach (var width in this.MaxColumnWidths)
                    SepBuilder.Append(this.PadStringAlgorithm("-", "-", width));

            return SepBuilder.ToString();
        }

        /// <summary>
        /// Generate formatted string with optional callback function for progress monitoring.
        /// </summary>
        public string ToFormattedString(int interval = int.MaxValue, Action<int> CallBack = null)
        {
            string MajorSep = this.GenerateSeparators(Separator.Major);
            string MinorSep = this.GenerateSeparators(Separator.Minor);

            int ColumnIndex = 0;
            for (int index = 0; index < this.StringTemps.Count; index++)
            {
                string StringTemp = this.StringTemps[index];

                if (this.SeparatorMap.TryGetValue(index, out Separator sep))
                {
                    if (sep == Separator.Major)
                        this.FormatBuilder.Append(MajorSep);
                    else if (sep == Separator.Minor)
                        this.FormatBuilder.Append(MinorSep);
                    this.FormatBuilder.AppendLine();
                    ColumnIndex = 0;
                }
                else
                {
                    if (StringTemp == Environment.NewLine)
                    {
                        ColumnIndex = 0;
                        this.FormatBuilder.AppendLine();
                    }

                    else
                    {
                        float Width = (ColumnIndex < this.MaxColumnWidths.Count) ? this.MaxColumnWidths[ColumnIndex] : 0f;
                        this.FormatBuilder.Append(this.PadStringAlgorithm(StringTemp, StringFormatter.PadString, Width));
                        ColumnIndex++;
                    }
                }

                if (CallBack != null && index % interval == 0)
                    CallBack.Invoke(index);
            }

            return this.FormatBuilder.ToString();
        }

        private bool _disposed = false;

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (this._disposed)
                return;

            if (disposing)
            {

                this.StringTemps?.Clear();
                this.StringTemps = null;
                this.FormatBuilder?.Clear();
                this.FormatBuilder = null;
                this.SeparatorMap?.Clear();
                this.SeparatorMap = null;
            }

            this._disposed = true;
        }

    }
    
}
