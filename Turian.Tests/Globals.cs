global using System.Buffers.Binary;
global using System.Collections.Immutable;
global using System.Collections.ObjectModel;
global using System.Globalization;
global using System.Numerics;
global using System.Reflection;
global using System.Runtime.CompilerServices;
global using System.Security.Cryptography;
global using System.Text;
global using System.Text.Json;
global using System.Text.Json.Serialization;
global using System.Text.RegularExpressions;
global using System.Xml.Linq;
global using Gaya.Host;
global using Gaya.Plugin.Turian;
global using Gaya.Sdk;
global using Guinevere;
global using Microsoft.CodeAnalysis;
global using Microsoft.CodeAnalysis.Text;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.Extensions.Logging.Abstractions;
global using NSubstitute;
global using Silk.NET.Vulkan;
global using SkiaSharp;
global using Turian.Editor.Core;
global using Turian.Engine.Core;
global using Turian.Engine.UI;
global using Xunit;
global using GKey = Guinevere.KeyboardKey;
global using GMouseButton = Guinevere.MouseButton;
global using GuiColor = Guinevere.Color;

// Several test classes reset and rebuild the process-wide AssetDatabase singleton.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
