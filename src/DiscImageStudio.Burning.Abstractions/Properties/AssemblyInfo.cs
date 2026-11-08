using System.Runtime.CompilerServices;

// The Windows backend and its tests keep using the members that were internal before the
// contracts moved out of the Windows assembly (Task 13.1), with unchanged behaviour.
[assembly: InternalsVisibleTo("DiscImageStudio.Burning")]
[assembly: InternalsVisibleTo("DiscImageStudio.ArchitectureTests")]
