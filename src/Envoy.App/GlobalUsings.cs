// WPF projects drop these from the implicit usings (System.IO.Path clashes with System.Windows.Shapes.Path).
// Nothing in this app uses Shapes.Path from code, so import them globally.
global using System.IO;
global using System.Net.Http;
