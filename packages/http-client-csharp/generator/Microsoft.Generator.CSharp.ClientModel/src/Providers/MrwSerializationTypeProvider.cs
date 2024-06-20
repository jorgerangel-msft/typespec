// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.Generator.CSharp.ClientModel.Snippets;
using Microsoft.Generator.CSharp.Expressions;
using Microsoft.Generator.CSharp.Input;
using Microsoft.Generator.CSharp.Providers;
using Microsoft.Generator.CSharp.Snippets;
using Microsoft.Generator.CSharp.Statements;
using static Microsoft.Generator.CSharp.Snippets.Snippet;

namespace Microsoft.Generator.CSharp.ClientModel.Providers
{
    /// <summary>
    /// This class provides the set of serialization models, methods, and interfaces for a given model.
    /// </summary>
    internal sealed class MrwSerializationTypeProvider : TypeProvider
    {
        private const string PrivateAdditionalPropertiesPropertyDescription = "Keeps track of any properties unknown to the library.";
        private const string PrivateAdditionalPropertiesPropertyName = "_serializedAdditionalRawData";
        private const string JsonModelWriteCoreMethodName = "JsonModelWriteCore";
        private readonly ParameterProvider _utf8JsonWriterParameter = new("writer", $"The JSON writer.", typeof(Utf8JsonWriter));
        private readonly ParameterProvider _serializationOptionsParameter =
            new("options", $"The client options for reading and writing models.", typeof(ModelReaderWriterOptions));
        private readonly Utf8JsonWriterSnippet _utf8JsonWriterSnippet;
        private readonly ModelReaderWriterOptionsSnippet _mrwOptionsParameterSnippet;
        private readonly CSharpType _privateAdditionalPropertiesPropertyType = typeof(IDictionary<string, BinaryData>);
        private readonly CSharpType _jsonModelTInterface;
        private readonly CSharpType? _jsonModelObjectInterface;
        private readonly CSharpType _persistableModelTInterface;
        private readonly CSharpType? _persistableModelObjectInterface;
        private TypeProvider _model;
        private readonly InputModelType _inputModel;
        private readonly FieldProvider? _rawDataField;
        private readonly bool _isStruct;
        // Flag to determine if the model should override the serialization methods
        private readonly bool _shouldOverrideMethods;

        public MrwSerializationTypeProvider(TypeProvider model, InputModelType inputModel)
        {
            _model = model;
            _inputModel = inputModel;
            _isStruct = model.DeclarationModifiers.HasFlag(TypeSignatureModifiers.Struct);
            // Initialize the serialization interfaces
            _jsonModelTInterface = new CSharpType(typeof(IJsonModel<>), model.Type);
            _jsonModelObjectInterface = _isStruct ? (CSharpType)typeof(IJsonModel<object>) : null;
            _persistableModelTInterface = new CSharpType(typeof(IPersistableModel<>), model.Type);
            _persistableModelObjectInterface = _isStruct ? (CSharpType)typeof(IPersistableModel<object>) : null;
            _rawDataField = BuildRawDataField();
            _shouldOverrideMethods = _model.Inherits != null && _model.Inherits is { IsFrameworkType: false, Implementation: ModelProvider };
            _utf8JsonWriterSnippet = new Utf8JsonWriterSnippet(_utf8JsonWriterParameter);
            _mrwOptionsParameterSnippet = new ModelReaderWriterOptionsSnippet(_serializationOptionsParameter);

            Name = model.Name;
            Namespace = model.Namespace;
        }

        protected override TypeSignatureModifiers GetDeclarationModifiers() => _model.DeclarationModifiers;

        public override string RelativeFilePath => Path.Combine("src", "Generated", "Models", $"{Name}.Serialization.cs");
        public override string Name { get; }
        public override string Namespace { get; }

        /// <summary>
        /// Builds the fields for the model by adding the raw data field for serialization.
        /// </summary>
        /// <returns>The list of <see cref="FieldProvider"/> for the model.</returns>
        protected override FieldProvider[] BuildFields()
        {
            return _rawDataField != null ? [_rawDataField] : Array.Empty<FieldProvider>();
        }

        protected override MethodProvider[] BuildConstructors()
        {
            List<MethodProvider> constructors = new List<MethodProvider>();
            bool serializationCtorParamsMatch = false;
            bool ctorWithNoParamsExist = false;
            MethodProvider serializationConstructor = BuildSerializationConstructor();

            foreach (var ctor in _model.Constructors)
            {
                var initializationCtorParams = ctor.Signature.Parameters;

                // Check if the model constructor has no parameters
                if (!ctorWithNoParamsExist && !initializationCtorParams.Any())
                {
                    ctorWithNoParamsExist = true;
                }

                if (!serializationCtorParamsMatch)
                {
                    // Check if the model constructor parameters match the serialization constructor parameters
                    if (initializationCtorParams.SequenceEqual(serializationConstructor.Signature.Parameters))
                    {
                        serializationCtorParamsMatch = true;
                    }
                }
            }

            // Add the serialization constructor if it doesn't match any of the existing constructors
            if (!serializationCtorParamsMatch)
            {
                constructors.Add(serializationConstructor);
            }

            // Add an empty constructor if the model doesn't have one
            if (!ctorWithNoParamsExist)
            {
                constructors.Add(BuildEmptyConstructor());
            }

            return constructors.ToArray();
        }

        /// <summary>
        /// Builds the raw data field for the model to be used for serialization.
        /// </summary>
        /// <returns>The constructed <see cref="FieldProvider"/> if the model should generate the field.</returns>
        private FieldProvider? BuildRawDataField()
        {
            if (_isStruct)
            {
                return null;
            }

            var FieldProvider = new FieldProvider(
                modifiers: FieldModifiers.Private,
                type: _privateAdditionalPropertiesPropertyType,
                name: PrivateAdditionalPropertiesPropertyName);

            return FieldProvider;
        }

        /// <summary>
        /// Builds the serialization methods for the model.
        /// </summary>
        /// <returns>A list of serialization and deserialization methods for the model.</returns>
        protected override MethodProvider[] BuildMethods()
        {
            var jsonModelWriteCoreMethod = BuildJsonModelWriteCoreMethod();
            var methods = new List<MethodProvider>()
            {
                // Add JsonModel serialization methods
                BuildJsonModelWriteMethod(jsonModelWriteCoreMethod),
                jsonModelWriteCoreMethod,
                BuildJsonModelCreateMethod(),
                // Add PersistableModel serialization methods
                BuildPersistableModelWriteMethod(),
                BuildPersistableModelCreateMethod(),
                BuildPersistableModelGetFormatFromOptionsMethod()
            };

            if (_isStruct)
            {
                methods.Add(BuildJsonModelWriteMethodObjectDeclaration());
            }

            return [.. methods];
        }

        /// <summary>
        /// Builds the types that the model type serialization implements.
        /// </summary>
        /// <returns>An array of <see cref="CSharpType"/> types that the model implements.</returns>
        protected override CSharpType[] BuildImplements()
        {
            int interfaceCount = _jsonModelObjectInterface != null ? 2 : 1;
            CSharpType[] interfaces = new CSharpType[interfaceCount];
            interfaces[0] = _jsonModelTInterface;

            if (_jsonModelObjectInterface != null)
            {
                interfaces[1] = _jsonModelObjectInterface;
            }

            return interfaces;
        }

        /// <summary>
        /// Builds the <see cref="IJsonModel{T}"/> write method for the model.
        /// </summary>
        internal MethodProvider BuildJsonModelWriteMethod(MethodProvider jsonModelWriteCoreMethod)
        {
            // void IJsonModel<T>.Write(Utf8JsonWriter writer, ModelReaderWriterOptions options)
            return new MethodProvider
            (
              new MethodSignature(nameof(IJsonModel<object>.Write), null, MethodSignatureModifiers.None, null, null, [_utf8JsonWriterParameter, _serializationOptionsParameter], ExplicitInterface: _jsonModelTInterface),
              BuildJsonModelWriteMethodBody(jsonModelWriteCoreMethod),
              this
            );
        }

        /// <summary>
        /// Builds the <see cref="IJsonModel{T}"/> write method for the model object.
        /// </summary>
        internal MethodProvider BuildJsonModelWriteMethodObjectDeclaration()
        {
            // void IJsonModel<object>.Write(Utf8JsonWriter writer, ModelReaderWriterOptions options) => ((IJsonModel<T>)this).Write(writer, options);
            var castToT = This.CastTo(_jsonModelTInterface);
            return new MethodProvider
            (
              new MethodSignature(nameof(IJsonModel<object>.Write), null, MethodSignatureModifiers.None, null, null, [_utf8JsonWriterParameter, _serializationOptionsParameter], ExplicitInterface: _jsonModelObjectInterface),
              new InvokeInstanceMethodExpression(castToT, nameof(IJsonModel<object>.Write), [_utf8JsonWriterParameter, _serializationOptionsParameter]),
              this
            );
        }

        /// <summary>
        /// Builds the <see cref="IJsonModel{T}"/> write core method for the model.
        /// </summary>
        internal MethodProvider BuildJsonModelWriteCoreMethod()
        {
            MethodSignatureModifiers modifiers = MethodSignatureModifiers.Protected | MethodSignatureModifiers.Virtual;
            if (_shouldOverrideMethods)
            {
                modifiers = MethodSignatureModifiers.Protected | MethodSignatureModifiers.Override;
            }
            // void JsonModelWriteCore(Utf8JsonWriter writer, ModelReaderWriterOptions options)
            return new MethodProvider
            (
              new MethodSignature(JsonModelWriteCoreMethodName, null, modifiers, null, null, [_utf8JsonWriterParameter, _serializationOptionsParameter]),
              BuildJsonModelWriteCoreMethodBody(),
              this
            );
        }

        /// <summary>
        /// Builds the <see cref="IJsonModel{T}"/> create method for the model.
        /// </summary>
        internal MethodProvider BuildJsonModelCreateMethod()
        {
            ParameterProvider utf8JsonReaderParameter = new("reader", $"The JSON reader.", typeof(Utf8JsonReader), isRef: true);
            // T IJsonModel<T>.Create(ref Utf8JsonReader reader, ModelReaderWriterOptions options)
            var typeOfT = GetModelArgumentType(_jsonModelTInterface);
            return new MethodProvider
            (
              new MethodSignature(nameof(IJsonModel<object>.Create), null, MethodSignatureModifiers.None, typeOfT, null, new[] { utf8JsonReaderParameter, _serializationOptionsParameter }, ExplicitInterface: _jsonModelTInterface),
              // TO-DO: Call the base model ctor for now until the model properties are serialized https://github.com/microsoft/typespec/issues/3330
              Snippet.Return(new NewInstanceExpression(typeOfT, Array.Empty<ValueExpression>())),
              this
            );
        }

        /// <summary>
        /// Builds the <see cref="IPersistableModel{T}"/> write method.
        /// </summary>
        internal MethodProvider BuildPersistableModelWriteMethod()
        {
            // BinaryData IPersistableModel<T>.Write(ModelReaderWriterOptions options)
            var returnType = typeof(BinaryData);
            return new MethodProvider
            (
                new MethodSignature(nameof(IPersistableModel<object>.Write), null, MethodSignatureModifiers.None, returnType, null, new[] { _serializationOptionsParameter }, ExplicitInterface: _persistableModelTInterface),
                // TO-DO: Call the base model ctor for now until the model properties are serialized https://github.com/microsoft/typespec/issues/3330
                Snippet.Return(new NewInstanceExpression(returnType, [Snippet.Literal(_persistableModelTInterface.Name)])),
                this
            );
        }

        /// <summary>
        /// Builds the <see cref="IPersistableModel{T}"/> create method.
        /// </summary>
        internal MethodProvider BuildPersistableModelCreateMethod()
        {
            ParameterProvider dataParameter = new("data", $"The data to parse.", typeof(BinaryData));
            // IPersistableModel<T>.Create(BinaryData data, ModelReaderWriterOptions options)
            var typeOfT = GetModelArgumentType(_persistableModelTInterface);
            return new MethodProvider
            (
                new MethodSignature(nameof(IPersistableModel<object>.Create), null, MethodSignatureModifiers.None, typeOfT, null, new[] { dataParameter, _serializationOptionsParameter }, ExplicitInterface: _persistableModelTInterface),
                // TO-DO: Call the base model ctor for now until the model properties are serialized https://github.com/microsoft/typespec/issues/3330
                Snippet.Return(new NewInstanceExpression(typeOfT, Array.Empty<ValueExpression>())),
                this
            );
        }

        /// <summary>
        /// Builds the <see cref="IPersistableModel{T}"/> GetFormatFromOptions method.
        /// </summary>
        internal MethodProvider BuildPersistableModelGetFormatFromOptionsMethod()
        {
            ValueExpression jsonWireFormat = SystemSnippet.JsonFormatSerialization;
            // ModelReaderWriterFormat IPersistableModel<T>.GetFormatFromOptions(ModelReaderWriterOptions options)
            return new MethodProvider
            (
                new MethodSignature(nameof(IPersistableModel<object>.GetFormatFromOptions), null, MethodSignatureModifiers.None, typeof(string), null, new[] { _serializationOptionsParameter }, ExplicitInterface: _persistableModelTInterface),
                jsonWireFormat,
                this
            );
        }

        /// <summary>
        /// Builds the serialization constructor for the model.
        /// </summary>
        /// <returns>The constructed serialization constructor.</returns>
        internal MethodProvider BuildSerializationConstructor()
        {
            var serializationCtorParameters = BuildSerializationConstructorParameters();

            return new MethodProvider(
                signature: new ConstructorSignature(
                    Type,
                    $"Initializes a new instance of {Type:C}",
                    MethodSignatureModifiers.Internal,
                    serializationCtorParameters),
                bodyStatements: new MethodBodyStatement[]
                {
                    GetPropertyInitializers(serializationCtorParameters)
                },
                this);
        }

        /// <summary>
        /// Constructs the body of the JsonModel write method.
        /// </summary>
        /// <param name="jsonModelWriteCoreMethod">The json model write method provider.</param>
        /// <returns></returns>
        private MethodBodyStatement[] BuildJsonModelWriteMethodBody(MethodProvider jsonModelWriteCoreMethod)
        {
            var coreMethodSignature = jsonModelWriteCoreMethod.Signature;
            var coreMethodSignatureParameters = coreMethodSignature.Parameters.Select(p => (ValueExpression)p).ToList();

            return
            [
                _utf8JsonWriterSnippet.WriteStartObject(),
                new InvokeInstanceMethodStatement(This, coreMethodSignature.Name, coreMethodSignatureParameters),
                _utf8JsonWriterSnippet.WriteEndObject(),
            ];
        }

        /// <summary>
        /// Builds the method body for the json model write core method.
        /// </summary>
        /// <returns>An array of MethodBodyStatement representing body for the JsonModelWriteCore method.</returns>
        private MethodBodyStatement[] BuildJsonModelWriteCoreMethodBody()
        {
            return
            [
                CreateValidateJsonFormat(_persistableModelTInterface, SerializationFormatValidationType.Write),
                CallBaseJsonModelWriteCore(),
                CreateWritePropertiesStatements(),
                CreateWriteAdditionalRawDataStatement()
            ];
        }

        private MethodBodyStatement CallBaseJsonModelWriteCore()
        {
            // base.<JsonModelWriteCore>()
            return _shouldOverrideMethods ?
                new InvokeInstanceMethodStatement(Base, JsonModelWriteCoreMethodName, [_utf8JsonWriterParameter, _serializationOptionsParameter])
                : EmptyStatement;
        }

        private MethodBodyStatement GetPropertyInitializers(IReadOnlyList<ParameterProvider> parameters)
        {
            List<MethodBodyStatement> methodBodyStatements = new();

            foreach (var param in parameters)
            {
                if (param.Name == _rawDataField?.Name.ToVariableName())
                {
                    methodBodyStatements.Add(Assign(new MemberExpression(null, _rawDataField.Name), param));
                    continue;
                }

                ValueExpression initializationValue = param;
                var initializationStatement = Assign(new MemberExpression(null, param.Name.FirstCharToUpperCase()), initializationValue);
                if (initializationStatement != null)
                {
                    methodBodyStatements.Add(initializationStatement);
                }
            }

            return methodBodyStatements;
        }

        /// <summary>
        /// Builds the parameters for the serialization constructor by iterating through the input model properties.
        /// It then adds raw data field to the constructor if it doesn't already exist in the list of constructed parameters.
        /// </summary>
        /// <returns>The list of parameters for the serialization parameter.</returns>
        private List<ParameterProvider> BuildSerializationConstructorParameters()
        {
            List<ParameterProvider> constructorParameters = new List<ParameterProvider>();
            bool shouldAddRawDataField = _rawDataField != null;

            foreach (var property in _inputModel.Properties)
            {
                var parameter = new ParameterProvider(property);
                constructorParameters.Add(parameter);

                if (shouldAddRawDataField && string.Equals(parameter.Name, _rawDataField?.Name, StringComparison.OrdinalIgnoreCase))
                {
                    shouldAddRawDataField = false;
                }
            }

            // Append the raw data field if it doesn't already exist in the constructor parameters
            if (shouldAddRawDataField && _rawDataField != null)
            {
                constructorParameters.Add(new ParameterProvider(
                    _rawDataField.Name.ToVariableName(),
                    FormattableStringHelpers.FromString(PrivateAdditionalPropertiesPropertyDescription),
                    _rawDataField.Type));
            }

            return constructorParameters;
        }

        private MethodProvider BuildEmptyConstructor()
        {
            var accessibility = _isStruct ? MethodSignatureModifiers.Public : MethodSignatureModifiers.Internal;
            return new MethodProvider(
                signature: new ConstructorSignature(Type, $"Initializes a new instance of {Type:C} for deserialization.", accessibility, Array.Empty<ParameterProvider>()),
                bodyStatements: new MethodBodyStatement(),
                this);
        }

        /// <summary>
        /// Produces the validation body statements for the JSON serialization format.
        /// </summary>
        private MethodBodyStatement CreateValidateJsonFormat(CSharpType modelInterface, SerializationFormatValidationType validationType)
        {
            /*
                var format = options.Format == "W" ? GetFormatFromOptions(options) : options.Format;
                if (format != <formatValue>)
                {
                    throw new FormatException($"The model {nameof(ThisModel)} does not support '{format}' format.");
                }
            */
            MethodBodyStatement[] statements =
            [
                GetConcreteFormat(_mrwOptionsParameterSnippet, modelInterface, out VariableExpression format),
                new IfStatement(NotEqual(format, ModelReaderWriterOptionsSnippet.JsonFormat))
                {
                    ThrowValidationFailException(format, modelInterface.Arguments[0], validationType)
                },
            ];

            return statements;
        }

        private MethodBodyStatement GetConcreteFormat(ModelReaderWriterOptionsSnippet options, CSharpType iModelTInterface, out VariableExpression format)
        {
            var castSnippet = new StringSnippet(This.CastTo(iModelTInterface).Invoke(nameof(IPersistableModel<object>.GetFormatFromOptions), options));
            var condition = new TernaryConditionalExpression(
                Equal(options.Format, ModelReaderWriterOptionsSnippet.WireFormat),
                castSnippet,
                options.Format);
            var reference = new VariableExpression(castSnippet.Type, "format");
            format = reference;
            return Var(reference, condition);
        }

        /// <summary>
        /// Creates a <see cref="KeywordStatement"/> of type <see cref="FormatException"/> with a specific message indicating
        /// that the model does not support the specified serialization format.
        /// </summary>
        /// <param name="format">The serialization format.</param>
        /// <param name="typeOfT">The type of the model.</param>
        /// <param name="validationType">The type of validation (write or read).</param>
        /// <returns>The <see cref="MethodBodyStatement"/> representing the throw statement.</returns>
        private KeywordStatement ThrowValidationFailException(ValueExpression format, CSharpType typeOfT, SerializationFormatValidationType validationType)
            => Throw(New.Instance(
                typeof(FormatException),
                new FormattableStringExpression($"The model {{{0}}} does not support {(validationType == SerializationFormatValidationType.Write ? "writing" : "reading")} '{{{1}}}' format.",
                [
                    Nameof(typeOfT),
                    format
                ])));

        /// <summary>
        /// Constructs the body statements for the JsonModelWriteCore method containing the serialization for the model properties.
        /// </summary>
        private MethodBodyStatement[] CreateWritePropertiesStatements()
        {
            var propertyCount = _model.Properties.Count;
            var propertyStatements = new MethodBodyStatement[propertyCount];
            for (var i = 0; i < propertyCount; i++)
            {
                var prop = _model.Properties[i];
                var propertyMember = new MemberExpression(null, prop.Name);
                var propSerializationFormat = prop.SerializationInfo?.SerializationFormat ?? SerializationFormat.Default;

                // Generate the serialization statements for the property
                var writePropertySerializationStatements = new MethodBodyStatement[]
                {
                    _utf8JsonWriterSnippet.WritePropertyName(prop.Name.ToVariableName()),
                    CreateSerializationStatement(prop.Type, propertyMember, propSerializationFormat)
                };

                // Wrap the serialization statement in a check for whether the property is defined
                var wrapInIsDefinedStatement = WrapInIsDefined(prop, propertyMember, writePropertySerializationStatements);
                propertyStatements[i] = prop.IsReadOnly ?
                    WrapInCheckNotWireIfStatement(_mrwOptionsParameterSnippet.Format, wrapInIsDefinedStatement)
                    : wrapInIsDefinedStatement;
            }

            return propertyStatements;
        }

        /// <summary>
        /// Wraps the serialization statement in a condition check to ensure only initialized and required properties are serialized.
        /// </summary>
        /// <param name="propertyProvider">The model property.</param>
        /// <param name="propertyMemberExpression">The expression representing the property to serialize.</param>
        /// <param name="writePropertySerializationStatement">The serialization statement to conditionally execute.</param>
        /// <returns>A method body statement that includes condition checks before serialization.</returns>
        private MethodBodyStatement WrapInIsDefined(
            PropertyProvider propertyProvider,
            MemberExpression propertyMemberExpression,
            MethodBodyStatement writePropertySerializationStatement)
        {
            var propertyType = propertyProvider.Type;
            var propertySerialization = propertyProvider.SerializationInfo;
            var isPropRequired = propertySerialization?.IsRequired ?? false;

            if (propertyType.IsNullable)
            {
                writePropertySerializationStatement = CheckPropertyIsInitialized(
                propertyProvider,
                isPropRequired,
                propertyMemberExpression,
                writePropertySerializationStatement);
            }

            // Directly return the statement if the property is required or a non-nullable value type that is not JsonElement
            if (IsRequiredOrNonNullableValueType(propertyType, isPropRequired))
            {
                return writePropertySerializationStatement;
            }

            // Conditionally serialize based on whether the property is a collection or a single value
            return CreateConditionalSerializationStatement(propertyType, propertyMemberExpression, writePropertySerializationStatement);
        }

        private MethodBodyStatement CheckPropertyIsInitialized(
            PropertyProvider propertyProvider,
            bool isPropRequired,
            MemberExpression propertyMemberExpression,
            MethodBodyStatement writePropertySerializationStatements)
        {
            var propertyType = propertyProvider.Type;
            BoolSnippet propertyIsInitialized;
            var propertySerialization = propertyProvider.SerializationInfo;
            var propName = propertySerialization?.SerializedName ?? propertyProvider.Name;

            if (propertyType.IsCollection && !propertyType.IsReadOnlyMemory && isPropRequired)
            {
                propertyIsInitialized = And(NotEqual(propertyMemberExpression, Null),
                    OptionalSnippet.IsCollectionDefined(new StringSnippet(propertyMemberExpression)));
            }
            else
            {
                propertyIsInitialized = NotEqual(propertyMemberExpression, Null);
            }

            return new IfElseStatement(
                propertyIsInitialized,
                writePropertySerializationStatements,
                _utf8JsonWriterSnippet.WriteNull(propName));
        }

        /// <summary>
        /// Adds a `format != "W"` around the statement <paramref name="statement"/>.
        /// If the statement is not an IfStatement, we just create an IfStatement and return.
        /// If the statement is an IfStatement, we could add the condition to its condition which should simplify the generated code.
        /// </summary>
        private IfStatement WrapInCheckNotWireIfStatement(ValueExpression format, MethodBodyStatement statement)
        {
            var isNotWireCondition = NotEqual(format, ModelReaderWriterOptionsSnippet.WireFormat);
            if (statement is IfStatement ifStatement)
            {
                var updatedCondition = And(isNotWireCondition, new BoolSnippet(ifStatement.Condition));
                IfStatement updatedIf = new(updatedCondition, ifStatement.Inline, ifStatement.AddBraces)
                {
                    ifStatement.Body
                };
                return updatedIf;
            }

            return new IfStatement(isNotWireCondition)
            {
                statement
            };
        }

        /// <summary>
        /// Creates a serialization statement for the specified type.
        /// </summary>
        /// <param name="serializationType">The type being serialized.</param>
        /// <param name="value">The value to be serialized.</param>
        /// <param name="serializationFormat">The serialization format.</param>
        /// <returns>The serialization statement.</returns>
        /// <exception cref="NotSupportedException">Thrown when the serialization type is not supported.</exception>
        private MethodBodyStatement CreateSerializationStatement(
            CSharpType serializationType,
            ValueExpression value,
            SerializationFormat serializationFormat)
        {
            MethodBodyStatement? serializationStatement = null;
            switch (serializationType)
            {
                case var dictionaryType when dictionaryType.IsDictionary:
                    serializationStatement = CreateDictionarySerializationStatement(
                        new DictionarySnippet(dictionaryType.Arguments[0], dictionaryType.Arguments[1], value),
                        serializationFormat);
                    break;
                case var listType when listType.IsList || listType.IsArray:
                    serializationStatement = CreateListSerializationStatement(
                        GetEnumerableExpression(value, listType),
                        serializationFormat);
                    break;
                case var frameworkType when !frameworkType.IsCollection:
                    serializationStatement = SerializeValue(serializationType, serializationFormat, value);
                    break;
            }

            return serializationStatement ?? throw new NotSupportedException($"Serialization of type {serializationType.Name} is not supported.");
        }

        private MethodBodyStatement CreateDictionarySerializationStatement(
            DictionarySnippet dictionary,
            SerializationFormat serializationFormat)
        {
            return new[]
            {
                _utf8JsonWriterSnippet.WriteStartObject(),
                new ForeachStatement("item", dictionary, out KeyValuePairSnippet keyValuePair)
                {
                    _utf8JsonWriterSnippet.WritePropertyName(keyValuePair.Key),
                    TypeRequiresNullCheckInSerialization(keyValuePair.ValueType) ?
                    new IfStatement(Equal(keyValuePair.Value, Null)) { _utf8JsonWriterSnippet.WriteNullValue(), Continue }: EmptyStatement,
                    CreateSerializationStatement(keyValuePair.ValueType, keyValuePair.Value, serializationFormat)
                },
                _utf8JsonWriterSnippet.WriteEndObject()
            };
        }

        private MethodBodyStatement CreateListSerializationStatement(
            EnumerableSnippet array,
            SerializationFormat serializationFormat)
        {
            return new[]
            {
                _utf8JsonWriterSnippet.WriteStartArray(),
                new ForeachStatement("item", array, out VariableExpression item)
                {
                    TypeRequiresNullCheckInSerialization(item.Type) ?
                    new IfStatement(Equal(item, Null)) { _utf8JsonWriterSnippet.WriteNullValue(), Continue } : EmptyStatement,
                    CreateSerializationStatement(item.Type, item, serializationFormat)
                },
                _utf8JsonWriterSnippet.WriteEndArray()
            };
        }

        private MethodBodyStatement? SerializeValue(
            CSharpType type,
            SerializationFormat serializationFormat,
            ValueExpression value)
        {
            return type switch
            {
                { SerializeAs: not null } or { IsFrameworkType: true } =>
                    SerializeFrameworkTypeValue(type, serializationFormat, value, type.SerializeAs ?? type.FrameworkType),
                { Implementation: EnumProvider enumProvider } =>
                    SerializeEnumProvider(enumProvider, type, value),
                { Implementation: ModelProvider modelProvider } =>
                    _utf8JsonWriterSnippet.WriteObjectValue(new TypeProviderSnippet(modelProvider, value), options: _mrwOptionsParameterSnippet),
                _ => null
            };
        }

        private MethodBodyStatement? SerializeEnumProvider(
            EnumProvider enumProvider,
            CSharpType type,
            ValueExpression value)
        {
            var enumerableSnippet = new EnumerableSnippet(type, value.NullableStructValue(type));
            if ((EnumIsIntValueType(enumProvider) && !enumProvider.IsExtensible) || EnumIsNumericValueType(enumProvider))
            {
                return _utf8JsonWriterSnippet.WriteNumberValue(enumProvider.ToSerial(enumerableSnippet));
            }
            else
            {
                return _utf8JsonWriterSnippet.WriteStringValue(enumProvider.ToSerial(enumerableSnippet));
            }
        }

        private MethodBodyStatement SerializeFrameworkTypeValue(
            CSharpType type,
            SerializationFormat serializationFormat,
            ValueExpression value,
            Type frameworkType)
        {
            if (frameworkType == typeof(JsonElement))
            {
                return new JsonElementSnippet(value).WriteTo(_utf8JsonWriterSnippet);
            }

            if (frameworkType == typeof(Nullable<>))
            {
                frameworkType = type.Arguments[0].FrameworkType;
            }

            value = value.NullableStructValue(type);

            if (frameworkType == typeof(decimal) ||
                frameworkType == typeof(double) ||
                frameworkType == typeof(float) ||
                frameworkType == typeof(long) ||
                frameworkType == typeof(int) ||
                frameworkType == typeof(short) ||
                frameworkType == typeof(sbyte) ||
                frameworkType == typeof(byte))
            {
                return _utf8JsonWriterSnippet.WriteNumberValue(value);
            }

            if (frameworkType == typeof(object))
            {
                return _utf8JsonWriterSnippet.WriteObjectValue(new FrameworkTypeSnippet(frameworkType, value), _mrwOptionsParameterSnippet);
            }

            if (frameworkType == typeof(string) || frameworkType == typeof(char) || frameworkType == typeof(Guid))
            {
                return _utf8JsonWriterSnippet.WriteStringValue(value);
            }

            if (frameworkType == typeof(bool))
            {
                return _utf8JsonWriterSnippet.WriteBooleanValue(value);
            }

            if (frameworkType == typeof(byte[]))
            {
                return _utf8JsonWriterSnippet.WriteBase64StringValue(value, serializationFormat.ToFormatSpecifier());
            }

            if (frameworkType == typeof(DateTimeOffset) || frameworkType == typeof(DateTime) || frameworkType == typeof(TimeSpan))
            {
                var format = serializationFormat.ToFormatSpecifier();

                if (serializationFormat is SerializationFormat.Duration_Seconds)
                {
                    return _utf8JsonWriterSnippet.WriteNumberValue(ConvertSnippet.InvokeToInt32(new TimeSpanSnippet(value).InvokeToString(format)));
                }

                if (serializationFormat is SerializationFormat.Duration_Seconds_Float or SerializationFormat.Duration_Seconds_Double)
                {
                    return _utf8JsonWriterSnippet.WriteNumberValue(ConvertSnippet.InvokeToDouble(new TimeSpanSnippet(value).InvokeToString(format)));
                }

                if (serializationFormat is SerializationFormat.DateTime_Unix)
                {
                    return _utf8JsonWriterSnippet.WriteNumberValue(value, format);
                }

                return format is not null
                    ? _utf8JsonWriterSnippet.WriteStringValue(value, format)
                    : _utf8JsonWriterSnippet.WriteStringValue(value);
            }

            if (frameworkType == typeof(IPAddress))
            {
                return _utf8JsonWriterSnippet.WriteStringValue(value.InvokeToString());
            }

            if (frameworkType == typeof(Uri))
            {
                return _utf8JsonWriterSnippet.WriteStringValue(new MemberExpression(value, nameof(Uri.AbsoluteUri)));
            }

            if (frameworkType == typeof(BinaryData))
            {
                var binaryDataValue = new BinaryDataSnippet(value);
                if (serializationFormat is SerializationFormat.Bytes_Base64 or SerializationFormat.Bytes_Base64Url)
                {
                    return _utf8JsonWriterSnippet.WriteBase64StringValue(new BinaryDataSnippet(value).ToArray(), serializationFormat.ToFormatSpecifier());
                }

                return _utf8JsonWriterSnippet.WriteBinaryData(binaryDataValue);
            }
            if (frameworkType == typeof(Stream))
            {
                return _utf8JsonWriterSnippet.WriteBinaryData(BinaryDataSnippet.FromStream(value, false));
            }

            throw new NotSupportedException($"Framework type {frameworkType} serialization not supported.");
        }

        private EnumerableSnippet GetEnumerableExpression(ValueExpression expression, CSharpType enumerableType)
        {
            CSharpType itemType = enumerableType.IsReadOnlyMemory ? new CSharpType(typeof(ReadOnlySpan<>), enumerableType.Arguments[0]) :
                enumerableType.ElementType;

            return new EnumerableSnippet(itemType, expression);
        }

        private bool IsRequiredOrNonNullableValueType(CSharpType propertyType, bool isRequired)
            => isRequired || (!propertyType.IsNullable && propertyType.IsValueType && !propertyType.Equals(typeof(JsonElement)));

        private MethodBodyStatement CreateConditionalSerializationStatement(CSharpType propertyType, MemberExpression propertyMemberExpression, MethodBodyStatement writePropertySerializationStatement)
        {
            var condition = propertyType.IsCollection && !propertyType.IsReadOnlyMemory
                ? OptionalSnippet.IsCollectionDefined(new StringSnippet(propertyMemberExpression))
                : OptionalSnippet.IsDefined(new StringSnippet(propertyMemberExpression));

            return new IfStatement(condition) { writePropertySerializationStatement };
        }

        /// <summary>
        /// Builds the JSON write core body statement for the additional raw data.
        /// </summary>
        /// <returns>The method body statement that writes the additional raw data.</returns>
        private MethodBodyStatement CreateWriteAdditionalRawDataStatement()
        {
            if (_rawDataField == null)
            {
                return EmptyStatement;
            }

            var rawDataMemberExp = new MemberExpression(null, _rawDataField.Name);
            var rawDataDictionaryExp = new DictionarySnippet(_rawDataField.Type.Arguments[0], _rawDataField.Type.Arguments[1], rawDataMemberExp);
            var forEachStatement = new ForeachStatement("item", rawDataDictionaryExp, out KeyValuePairSnippet item)
            {
                _utf8JsonWriterSnippet.WritePropertyName(item.Key),
                CreateSerializationStatement(_rawDataField.Type.Arguments[1], item.Value, SerializationFormat.Default),
            };

            var ifNotEqualToNullStatement = new IfStatement(NotEqual(rawDataDictionaryExp, Null))
            {
                forEachStatement,
            };

            return WrapInCheckNotWireIfStatement(_mrwOptionsParameterSnippet.Format, ifNotEqualToNullStatement);
        }

        /// <summary>
        /// Attempts to get the model argument type from the model interface.
        /// </summary>
        /// <param name="modelInterface">The <see cref="CSharpType"/> that represents the model interface.</param>
        /// <returns>The first argument type of <paramref name="modelInterface"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the <paramref name="modelInterface"/> contains no arguments.</exception>
        private CSharpType GetModelArgumentType(CSharpType modelInterface)
        {
            var interfaceArgs = modelInterface.Arguments;
            if (!interfaceArgs.Any())
            {
                throw new InvalidOperationException($"Expected at least 1 argument for {modelInterface}, but found none.");
            }

            return interfaceArgs[0];
        }

        /// <summary>
        /// Determines if the type requires a null check in serialization.
        /// </summary>
        /// <param name="type">The <see cref="CSharpType"/> to validate.</param>
        /// <returns><c>true</c> if the type requires a null check.</returns>
        private bool TypeRequiresNullCheckInSerialization(CSharpType type)
        {
            if (type.IsCollection)
            {
                return true;
            }
            else if (type.IsNullable && type.IsValueType) // nullable value type
            {
                return true;
            }
            else if (!type.IsValueType && type.IsFrameworkType
                && (type.FrameworkType != typeof(string) || type.FrameworkType != typeof(byte[])))
            {
                // reference type, excluding string or byte[]
                return true;
            }

            return false;
        }

        private bool EnumIsIntValueType(EnumProvider enumProvider)
        {
            var frameworkType = enumProvider.ValueType;
            return frameworkType.Equals(typeof(int)) || frameworkType.Equals(typeof(long));
        }

        private bool EnumIsFloatValueType(EnumProvider enumProvider)
        {
            var frameworkType = enumProvider.ValueType;
            return frameworkType.Equals(typeof(float)) || frameworkType.Equals(typeof(double));
        }

        private bool EnumIsNumericValueType(EnumProvider enumProvider)
        {
            return EnumIsIntValueType(enumProvider) || EnumIsFloatValueType(enumProvider);
        }
    }
}
