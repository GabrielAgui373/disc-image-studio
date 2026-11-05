using System.Runtime.CompilerServices;

// The media module owns the historical command adapter and consumes a few implementation types
// while the extraction preserves the existing public API. Expanding those types publicly would
// make the mechanical assembly split an API redesign.
[assembly: InternalsVisibleTo("DiscImageStudio.Dvd")]
