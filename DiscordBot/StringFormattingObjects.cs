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
                // \u5168\u5F62\uFF08CJK / \u5047\u540D / \u8AFA\u6587 / \u5168\u5F62\u7B26\u865F\uFF09\u7B97 2 \u683C\uFF0C\u5176\u4ED6\u7B97 1 \u683C\u3002
                // \u4EE5\u524D\u6709\u4E00\u689D c >= 0x20000 \u7684\u5224\u65B7\uFF1Achar \u6700\u5927\u53EA\u5230 0xFFFF\uFF0C\u6C38\u9060\u4E0D\u6210\u7ACB\uFF1B\u5F8C\u9762\u56DB\u500B\u5206\u652F\u4E5F\u5168\u90E8\u90FD\u662F +1\u3002
                sum += c switch
                {
                    >= '\u1100' and <= '\u11FF' => 2f, // \u8AFA\u6587\u5B57\u6BCD
                    >= '\u3040' and <= '\u30FF' => 2f, // \u5E73\u5047\u540D\u3001\u7247\u5047\u540D
                    >= '\u31F0' and <= '\u31FF' => 2f, // \u7247\u5047\u540D\u64F4\u5145
                    >= '\u3400' and <= '\u4DBF' => 2f, // CJK \u64F4\u5145 A
                    >= '\u4E00' and <= '\u9FFF' => 2f, // CJK
                    >= '\uAC00' and <= '\uD7AF' => 2f, // \u8AFA\u6587\u97F3\u7BC0
                    >= '\uFF01' and <= '\uFF60' => 2f, // \u5168\u5F62\u7B26\u865F
                    _ => 1f,
                };
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
            this.MaxColumnWidths.Clear();
            this.SeparatorMap.Clear();
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
        public string ToFormattedString(int interval = int.MaxValue, Action<int>? CallBack = null)
        {
            // Previously not cleared, so calling it twice duplicated the output
            this.FormatBuilder.Clear();

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

                this.StringTemps.Clear();
                this.FormatBuilder.Clear();
                this.SeparatorMap.Clear();
                this.MaxColumnWidths.Clear();
            }

            this._disposed = true;
        }

    }
    
}
