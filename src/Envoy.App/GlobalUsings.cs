// WPF projects drop these from the implicit usings (System.IO.Path clashes with System.Windows.Shapes.Path).
// Code that needs Shapes.Path (MainWindow's marginal drawing) names it in full.
global using System.IO;
global using System.Net.Http;
