using System;

namespace DvdRipper.Models
{
    /// <summary>
    /// Represents a single DVD title (program chain) discovered on the disc.
    /// </summary>
    public class TitleInfo(int number, int durationSeconds)
    {

        /// <summary>
        /// Gets the numeric identifier of the title (e.g. 1, 2, 3...).
        /// </summary>
        public int Number { get; } = number;

        /// <summary>
        /// Gets the length of the title in seconds. Zero when unknown.
        /// </summary>
        public int DurationSeconds { get; } = durationSeconds;
    }
}