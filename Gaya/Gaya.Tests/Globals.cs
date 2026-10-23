global using System.Diagnostics;
global using System.Globalization;
global using System.IO.Compression;
global using System.Numerics;
global using System.Reflection;
global using System.Text.Json.Nodes;
global using Gaya.Host;
global using Gaya.Packages.Editor;
global using Gaya.Sdk;
global using Guinevere;
global using MASS4.Bricks;
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Logging.Abstractions;
global using NSubstitute;
global using SkiaSharp;
global using Xunit;
global using GMouseButton = Guinevere.MouseButton;
global using GuiColor = Guinevere.Color;

// Test classes run in parallel; those sharing process-wide state opt into the serial SerialTests collection.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.Collections)]
