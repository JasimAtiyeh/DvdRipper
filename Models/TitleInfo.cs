using System;

namespace DvdRipper.Models
{
    /// <summary>
    /// Represents a single DVD title (program chain) discovered on the disc.
    /// </summary>
    public class TitleInfo
    {
        public TitleInfo(int number, int durationSeconds)
        {
            Number = number;
            DurationSeconds = durationSeconds;
        }

        /// <summary>
        /// Gets the numeric identifier of the title (e.g. 1, 2, 3...).
        /// </summary>
        public int Number { get; }

        /// <summary>
        /// Gets the length of the title in seconds. Zero when unknown.
        /// </summary>
        public int DurationSeconds { get; }

        public override string ToString()
        {
            if (DurationSeconds > 0)
            {
                TimeSpan span = TimeSpan.FromSeconds(DurationSeconds);
                return $"Title {Number} — {span:hh\\:mm\\:ss}";
            }
            return $"Title {Number}";
        }
    }
}