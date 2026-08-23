using System;
using System.Reflection;
using System.IO;

class Program
{
    static void Main(string[] args)
    {
        string dllPath = @"D:\Projects\NoSteamLauncher\bin\Release\net8.0\NoSteamLauncher.dll";
        var asm = Assembly.LoadFrom(dllPath);
        var resources = asm.GetManifestResourceNames();
        
        Console.WriteLine("=== Manifest Resources ===");
        foreach (var r in resources)
        {
            Console.WriteLine(r);
        }
    }
}