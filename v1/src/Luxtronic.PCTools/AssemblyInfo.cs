using System.Runtime.CompilerServices;
using System.Windows;

// Lets the xUnit test project (tests/Luxtronic.PCTools.Tests) call the internal pure-logic
// methods extracted for testability, without widening the public API surface.
[assembly: InternalsVisibleTo("Luxtronic.PCTools.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
