// UseWPF drops System.IO from the implicit usings (it clashes with System.Windows.Shapes.Path).
// We don't use WPF shapes, so bring it back for the whole project.
global using System.IO;
