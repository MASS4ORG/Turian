global using System.Buffers.Binary;
global using System.Collections.Immutable;
global using System.Collections.ObjectModel;
global using System.Diagnostics;
global using System.Formats.Tar;
global using System.Globalization;
global using System.IO.Compression;
global using System.Numerics;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Nodes;
global using System.Text.Json.Serialization;
global using System.Text.RegularExpressions;
global using System.Xml.Linq;
global using Autoformers;
global using Gaya.Host;
global using Gaya.Packages;
global using Gaya.Packages.Editor;
global using Gaya.Sdk;
global using Guinevere;
global using MASS4.Attributes;
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.CSharp;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Logging.Abstractions;
global using NSubstitute;
global using SkiaSharp;
global using Xunit;
global using GKey = Guinevere.KeyboardKey;
global using GMouseButton = Guinevere.MouseButton;
global using GuiColor = Guinevere.Color;

// Test classes run in parallel; those sharing process-wide state opt into the serial SerialTests collection.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.Collections)]
