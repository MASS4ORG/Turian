#pragma warning disable CS0414

namespace Turian.Tests;

/// <summary>
/// Test the (de)serialization process
/// </summary>
public class ObjectJsonSerializerTests
{
    /// <summary>
    /// Test subclass that derived from IdClass
    /// </summary>
    [TypeId("a3000003-0000-4000-8000-000000000001")]
    internal sealed class InnerTestClass : IdClass
    {
        public int InnerValue { get; set; } = 1;
    }

    /// <summary>
    /// Test class that derived from IdClass
    /// </summary>
    [TypeId("a3000003-0000-4000-8000-000000000002")]
    internal class TestClass : IdClass
    {

        public int PublicProperty { get; set; } = 1;
        protected int ProtectedProperty { get; set; } = 1;
        int PrivateProperty { get; set; } = 1;
        internal int InternalProperty { get; set; } = 1;

        [JsonIgnore]
        public int PublicPropertyJsonIgnore { get; set; } = 1;

        [JsonIgnore]
        protected int ProtectedPropertyJsonIgnore { get; set; } = 1;

        [JsonIgnore]
        int PrivatePropertyJsonIgnore { get; set; } = 1;

        [JsonIgnore]
        internal int InternalPropertyJsonIgnore { get; set; } = 1;

        [JsonInclude]
        public int PublicPropertyJsonInclude { get; set; } = 1;

        [JsonInclude]
        protected int ProtectedPropertyJsonInclude { get; set; } = 1;

        [JsonInclude]
        int PrivatePropertyJsonInclude { get; set; } = 1;

        [JsonInclude]
        internal int InternalPropertyJsonInclude { get; set; } = 1;

        public int PublicField = 1;
        protected int ProtectedField = 1;
        int privateField = 1;
        internal int InternalField = 1;

        [JsonIgnore]
        public int PublicFieldJsonIgnore = 1;

        [JsonIgnore]
        protected int ProtectedFieldJsonIgnore = 1;

        [JsonIgnore]
        int privateFieldJsonIgnore = 1;

        [JsonIgnore]
        internal int InternalFieldJsonIgnore = 1;

        [JsonInclude]
        public int PublicFieldJsonInclude = 1;

        [JsonInclude]
        protected int ProtectedFieldJsonInclude = 1;

        [JsonInclude]
        int privateFieldJsonInclude = 1;

        [JsonInclude]
        internal int InternalFieldJsonInclude = 1;

        public Collection<InnerTestClass> ListInnerTestClass =
            [
                new InnerTestClass(),
                new InnerTestClass(),
                new InnerTestClass(),
                new InnerTestClass(),
            ];

        public InnerTestClass InnerObject { get; set; } = new InnerTestClass();
        public Collection<int> IntList { get; set; } = [1, 2, 3];
        public Dictionary<string, int> IntDict { get; set; } = new() { { "One", 1 }, { "Two", 2 } };

        public int GetPrivatePropertyValue() => PrivateProperty;

        public int GetProtectedPropertyValue() => ProtectedProperty;

        public int GetInternalPropertyValue() => InternalProperty;

        public int GetPrivatePropertyValueJsonIgnore() => PrivatePropertyJsonIgnore;

        public int GetProtectedPropertyValueJsonIgnore() => ProtectedPropertyJsonIgnore;

        public int GetInternalPropertyValueJsonIgnore() => InternalPropertyJsonIgnore;

        public int GetPrivatePropertyValueJsonInclude() => PrivatePropertyJsonInclude;

        public int GetProtectedPropertyValueJsonInclude() => ProtectedPropertyJsonInclude;

        public int GetInternalPropertyValueJsonInclude() => InternalPropertyJsonInclude;

        public int GetPrivateFieldValue() => privateField;

        public int GetProtectedFieldValue() => ProtectedField;

        public int GetInternalFieldValue() => InternalField;

        public int GetPrivateFieldValueJsonIgnore() => privateFieldJsonIgnore;

        public int GetProtectedFieldValueJsonIgnore() => ProtectedFieldJsonIgnore;

        public int GetInternalFieldValueJsonIgnore() => InternalFieldJsonIgnore;

        public int GetPrivateFieldValueJsonInclude() => privateFieldJsonInclude;

        public int GetProtectedFieldValueJsonInclude() => ProtectedFieldJsonInclude;

        public int GetInternalFieldValueJsonInclude() => InternalFieldJsonInclude;

        /// <summary>
        /// Alter ALL internal values to allow tests to verify if it was properly
        /// (de)serialized or it's the default constructor values
        /// </summary>
        public void AlterValues()
        {
            PublicProperty = 10;
            ProtectedProperty = 10;
            PrivateProperty = 10;
            InternalProperty = 10;

            PublicPropertyJsonIgnore = 10;
            ProtectedPropertyJsonIgnore = 10;
            PrivatePropertyJsonIgnore = 10;
            InternalPropertyJsonIgnore = 10;

            PublicPropertyJsonInclude = 10;
            ProtectedPropertyJsonInclude = 10;
            PrivatePropertyJsonInclude = 10;
            InternalPropertyJsonInclude = 10;

            PublicField = 10;
            ProtectedField = 10;
            privateField = 10;
            InternalField = 10;

            PublicFieldJsonIgnore = 10;
            ProtectedFieldJsonIgnore = 10;
            privateFieldJsonIgnore = 10;
            InternalFieldJsonIgnore = 10;

            PublicFieldJsonInclude = 10;
            ProtectedFieldJsonInclude = 10;
            privateFieldJsonInclude = 10;
            InternalFieldJsonInclude = 10;

            IntList = [10, 20, 30];
        }
    }

    readonly JsonSerializerOptions options;
    readonly TestClass objTest;
    readonly string jsonTest;

    /// <summary>
    /// Constructor and test setup
    /// </summary>
    public ObjectJsonSerializerTests()
    {
        options = new JsonSerializerOptions();
        options.Converters.Add(new ObjectJsonSerializer<TestClass>());

        objTest = new TestClass();
        objTest.AlterValues();

        // Serializing object
        jsonTest = JsonSerializer.Serialize(objTest, options);
    }

    /// <summary>
    /// Test if the property/field exist or not in the serialized json
    /// and, if exist, if the value is the expected
    /// </summary>
    /// <param name="propertyName"></param>
    /// <param name="mustContain"></param>
    /// <param name="value"></param>
    [Theory]
    [InlineData("PublicProperty", true, 10)]
    [InlineData("PrivateProperty", false, 1)]
    [InlineData("ProtectedProperty", false, 1)]
    [InlineData("InternalProperty", false, 1)]
    [InlineData("PublicPropertyJsonIgnore", false, 1)]
    [InlineData("PrivatePropertyJsonIgnore", false, 1)]
    [InlineData("ProtectedPropertyJsonIgnore", false, 1)]
    [InlineData("InternalPropertyJsonIgnore", false, 1)]
    [InlineData("PublicPropertyJsonInclude", true, 10)]
    [InlineData("PrivatePropertyJsonInclude", true, 10)]
    [InlineData("ProtectedPropertyJsonInclude", true, 10)]
    [InlineData("InternalPropertyJsonInclude", true, 10)]
    [InlineData("PublicField", true, 10)]
    [InlineData("privateField", false, 1)]
    [InlineData("ProtectedField", false, 1)]
    [InlineData("InternalField", false, 1)]
    [InlineData("PublicFieldJsonIgnore", false, 1)]
    [InlineData("privateFieldJsonIgnore", false, 1)]
    [InlineData("ProtectedFieldJsonIgnore", false, 1)]
    [InlineData("InternalFieldJsonIgnore", false, 1)]
    [InlineData("PublicFieldJsonInclude", true, 10)]
    [InlineData("privateFieldJsonInclude", true, 10)]
    [InlineData("ProtectedFieldJsonInclude", true, 10)]
    [InlineData("InternalFieldJsonInclude", true, 10)]
    public void SerializationPublicAndAttributeValues(
        string propertyName,
        bool mustContain,
        int value
    )
    {
        // Asserting the serialized string
        if (mustContain)
        {
            Assert.Contains(
                $"\"{propertyName}\":{value}",
                jsonTest,
                StringComparison.InvariantCulture
            );
        }
        else
        {
            Assert.DoesNotContain(
                $"\"{propertyName}\"",
                jsonTest,
                StringComparison.InvariantCulture
            );
        }
    }

    /// <summary>
    /// Test if the property/field exist in the (de)serialized json and if the value is the expected
    /// </summary>
    [Fact]
    public void DeserializationTest()
    {
        var obj = JsonSerializer.Deserialize<TestClass>(jsonTest, options);

        Assert.NotNull(obj);
        Assert.Equal(10, obj.PublicProperty);
        Assert.Equal(1, obj.GetProtectedPropertyValue());
        Assert.Equal(1, obj.GetPrivatePropertyValue());
        Assert.Equal(1, obj.GetInternalPropertyValue());
        Assert.Equal(1, obj.GetProtectedPropertyValueJsonIgnore());
        Assert.Equal(1, obj.GetPrivatePropertyValueJsonIgnore());
        Assert.Equal(1, obj.GetInternalPropertyValueJsonIgnore());
        Assert.Equal(10, obj.GetProtectedPropertyValueJsonInclude());
        Assert.Equal(10, obj.GetPrivatePropertyValueJsonInclude());
        Assert.Equal(10, obj.GetInternalPropertyValueJsonInclude());

        Assert.Equal(1, obj.GetProtectedFieldValue());
        Assert.Equal(1, obj.GetPrivateFieldValue());
        Assert.Equal(1, obj.GetInternalFieldValue());
        Assert.Equal(1, obj.GetProtectedFieldValueJsonIgnore());
        Assert.Equal(1, obj.GetPrivateFieldValueJsonIgnore());
        Assert.Equal(1, obj.GetInternalFieldValueJsonIgnore());
        Assert.Equal(10, obj.GetProtectedFieldValueJsonInclude());
        Assert.Equal(10, obj.GetPrivateFieldValueJsonInclude());
        Assert.Equal(10, obj.GetInternalFieldValueJsonInclude());
        Assert.Equal(1, obj.InnerObject.InnerValue);
    }

    /// <summary>
    /// From a given original class, create a copy using seralization and deserialization
    /// and compare it's values
    /// </summary>
    [Fact]
    public void FullSerializationDeserializationTest()
    {
        var obj = JsonSerializer.Deserialize<TestClass>(jsonTest, options);

        Assert.NotNull(obj);
        Assert.Equal(objTest.PublicProperty, obj.PublicProperty);

        Assert.NotEqual(objTest.GetPrivatePropertyValue(), obj.GetPrivatePropertyValue());
        Assert.NotEqual(objTest.GetProtectedPropertyValue(), obj.GetProtectedPropertyValue());
        Assert.NotEqual(objTest.GetInternalPropertyValue(), obj.GetInternalPropertyValue());

        Assert.NotEqual(
            objTest.GetProtectedPropertyValueJsonIgnore(),
            obj.GetProtectedPropertyValueJsonIgnore()
        );
        Assert.NotEqual(
            objTest.GetProtectedPropertyValueJsonIgnore(),
            obj.GetProtectedPropertyValueJsonIgnore()
        );
        Assert.NotEqual(
            objTest.GetPrivatePropertyValueJsonIgnore(),
            obj.GetPrivatePropertyValueJsonIgnore()
        );
        Assert.NotEqual(
            objTest.GetInternalPropertyValueJsonIgnore(),
            obj.GetInternalPropertyValueJsonIgnore()
        );

        Assert.Equal(
            objTest.GetProtectedPropertyValueJsonInclude(),
            obj.GetProtectedPropertyValueJsonInclude()
        );
        Assert.Equal(
            objTest.GetProtectedPropertyValueJsonInclude(),
            obj.GetProtectedPropertyValueJsonInclude()
        );
        Assert.Equal(
            objTest.GetPrivatePropertyValueJsonInclude(),
            obj.GetPrivatePropertyValueJsonInclude()
        );
        Assert.Equal(
            objTest.GetPrivatePropertyValueJsonInclude(),
            obj.GetPrivatePropertyValueJsonInclude()
        );

        Assert.NotEqual(objTest.GetPrivateFieldValue(), obj.GetPrivateFieldValue());
        Assert.NotEqual(objTest.GetProtectedFieldValue(), obj.GetProtectedFieldValue());
        Assert.NotEqual(objTest.GetInternalFieldValue(), obj.GetInternalFieldValue());

        Assert.NotEqual(
            objTest.GetProtectedFieldValueJsonIgnore(),
            obj.GetProtectedFieldValueJsonIgnore()
        );
        Assert.NotEqual(
            objTest.GetProtectedFieldValueJsonIgnore(),
            obj.GetProtectedFieldValueJsonIgnore()
        );
        Assert.NotEqual(
            objTest.GetPrivateFieldValueJsonIgnore(),
            obj.GetPrivateFieldValueJsonIgnore()
        );
        Assert.NotEqual(
            objTest.GetInternalFieldValueJsonIgnore(),
            obj.GetInternalFieldValueJsonIgnore()
        );

        Assert.Equal(
            objTest.GetProtectedFieldValueJsonInclude(),
            obj.GetProtectedFieldValueJsonInclude()
        );
        Assert.Equal(
            objTest.GetProtectedFieldValueJsonInclude(),
            obj.GetProtectedFieldValueJsonInclude()
        );
        Assert.Equal(
            objTest.GetPrivateFieldValueJsonInclude(),
            obj.GetPrivateFieldValueJsonInclude()
        );
        Assert.Equal(
            objTest.GetPrivateFieldValueJsonInclude(),
            obj.GetPrivateFieldValueJsonInclude()
        );

        Assert.Equal(objTest.InnerObject.InnerValue, obj.InnerObject.InnerValue);
        Assert.Equal(objTest.IntList, obj.IntList);
        Assert.Equal(objTest.IntDict, obj.IntDict);
    }

    /// <summary>
    /// Given a serialized JSON, serialize the class again and check if the jsons are equal
    /// </summary>
    [Fact]
    public void FullSerializationDeserializationTestJSon()
    {
        var deserializedObj = JsonSerializer.Deserialize<TestClass>(jsonTest, options);
        var json2 = JsonSerializer.Serialize(deserializedObj, options);

        Assert.Equal(jsonTest, json2);
    }
}
