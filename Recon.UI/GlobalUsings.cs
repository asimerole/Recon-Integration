// Resolve WPF vs WinForms ambiguities introduced by <UseWindowsForms>true</UseWindowsForms>
global using Application = System.Windows.Application;
global using CheckBox = System.Windows.Controls.CheckBox;
