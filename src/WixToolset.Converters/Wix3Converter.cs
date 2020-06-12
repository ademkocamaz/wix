// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Converters
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Xml;
    using System.Xml.Linq;
    using WixToolset.Data;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// WiX source code converter.
    /// </summary>
    public class Wix3Converter
    {
        private static readonly Regex AddPrefix = new Regex(@"^[^a-zA-Z_]", RegexOptions.Compiled);
        private static readonly Regex IllegalIdentifierCharacters = new Regex(@"[^A-Za-z0-9_\.]|\.{2,}", RegexOptions.Compiled); // non 'words' and assorted valid characters

        private const char XDocumentNewLine = '\n'; // XDocument normalizes "\r\n" to just "\n".
        private static readonly XNamespace WixNamespace = "http://wixtoolset.org/schemas/v4/wxs";
        private static readonly XNamespace WixUtilNamespace = "http://wixtoolset.org/schemas/v4/wxs/util";

        private static readonly XName ColumnElementName = WixNamespace + "Column";
        private static readonly XName CreateFolderElementName = WixNamespace + "CreateFolder";
        private static readonly XName CustomTableElementName = WixNamespace + "CustomTable";
        private static readonly XName DirectoryElementName = WixNamespace + "Directory";
        private static readonly XName FileElementName = WixNamespace + "File";
        private static readonly XName ExePackageElementName = WixNamespace + "ExePackage";
        private static readonly XName MsiPackageElementName = WixNamespace + "MsiPackage";
        private static readonly XName MspPackageElementName = WixNamespace + "MspPackage";
        private static readonly XName MsuPackageElementName = WixNamespace + "MsuPackage";
        private static readonly XName PayloadElementName = WixNamespace + "Payload";
        private static readonly XName UtilPermissionExElementName = WixUtilNamespace + "PermissionEx";
        private static readonly XName CustomActionElementName = WixNamespace + "CustomAction";
        private static readonly XName PropertyElementName = WixNamespace + "Property";
        private static readonly XName WixElementWithoutNamespaceName = XNamespace.None + "Wix";
        private static readonly XName IncludeElementWithoutNamespaceName = XNamespace.None + "Include";

        private static readonly Dictionary<string, XNamespace> OldToNewNamespaceMapping = new Dictionary<string, XNamespace>()
        {
            { "http://schemas.microsoft.com/wix/BalExtension", "http://wixtoolset.org/schemas/v4/wxs/bal" },
            { "http://schemas.microsoft.com/wix/ComPlusExtension", "http://wixtoolset.org/schemas/v4/wxs/complus" },
            { "http://schemas.microsoft.com/wix/DependencyExtension", "http://wixtoolset.org/schemas/v4/wxs/dependency" },
            { "http://schemas.microsoft.com/wix/DifxAppExtension", "http://wixtoolset.org/schemas/v4/wxs/difxapp" },
            { "http://schemas.microsoft.com/wix/FirewallExtension", "http://wixtoolset.org/schemas/v4/wxs/firewall" },
            { "http://schemas.microsoft.com/wix/GamingExtension", "http://wixtoolset.org/schemas/v4/wxs/gaming" },
            { "http://schemas.microsoft.com/wix/IIsExtension", "http://wixtoolset.org/schemas/v4/wxs/iis" },
            { "http://schemas.microsoft.com/wix/MsmqExtension", "http://wixtoolset.org/schemas/v4/wxs/msmq" },
            { "http://schemas.microsoft.com/wix/NetFxExtension", "http://wixtoolset.org/schemas/v4/wxs/netfx" },
            { "http://schemas.microsoft.com/wix/PSExtension", "http://wixtoolset.org/schemas/v4/wxs/powershell" },
            { "http://schemas.microsoft.com/wix/SqlExtension", "http://wixtoolset.org/schemas/v4/wxs/sql" },
            { "http://schemas.microsoft.com/wix/TagExtension", "http://wixtoolset.org/schemas/v4/wxs/tag" },
            { "http://schemas.microsoft.com/wix/UtilExtension", WixUtilNamespace },
            { "http://schemas.microsoft.com/wix/VSExtension", "http://wixtoolset.org/schemas/v4/wxs/vs" },
            { "http://wixtoolset.org/schemas/thmutil/2010", "http://wixtoolset.org/schemas/v4/thmutil" },
            { "http://schemas.microsoft.com/wix/2009/Lux", "http://wixtoolset.org/schemas/v4/lux" },
            { "http://schemas.microsoft.com/wix/2006/wi", "http://wixtoolset.org/schemas/v4/wxs" },
            { "http://schemas.microsoft.com/wix/2006/localization", "http://wixtoolset.org/schemas/v4/wxl" },
            { "http://schemas.microsoft.com/wix/2006/libraries", "http://wixtoolset.org/schemas/v4/wixlib" },
            { "http://schemas.microsoft.com/wix/2006/objects", "http://wixtoolset.org/schemas/v4/wixobj" },
            { "http://schemas.microsoft.com/wix/2006/outputs", "http://wixtoolset.org/schemas/v4/wixout" },
            { "http://schemas.microsoft.com/wix/2007/pdbs", "http://wixtoolset.org/schemas/v4/wixpdb" },
            { "http://schemas.microsoft.com/wix/2003/04/actions", "http://wixtoolset.org/schemas/v4/wi/actions" },
            { "http://schemas.microsoft.com/wix/2006/tables", "http://wixtoolset.org/schemas/v4/wi/tables" },
            { "http://schemas.microsoft.com/wix/2006/WixUnit", "http://wixtoolset.org/schemas/v4/wixunit" },
        };

        private readonly Dictionary<XName, Action<XElement>> ConvertElementMapping;

        /// <summary>
        /// Instantiate a new Converter class.
        /// </summary>
        /// <param name="indentationAmount">Indentation value to use when validating leading whitespace.</param>
        /// <param name="errorsAsWarnings">Test errors to display as warnings.</param>
        /// <param name="ignoreErrors">Test errors to ignore.</param>
        public Wix3Converter(IMessaging messaging, int indentationAmount, IEnumerable<string> errorsAsWarnings = null, IEnumerable<string> ignoreErrors = null)
        {
            this.ConvertElementMapping = new Dictionary<XName, Action<XElement>>
            {
                { Wix3Converter.ColumnElementName, this.ConvertColumnElement },
                { Wix3Converter.CustomTableElementName, this.ConvertCustomTableElement },
                { Wix3Converter.DirectoryElementName, this.ConvertDirectoryElement },
                { Wix3Converter.FileElementName, this.ConvertFileElement },
                { Wix3Converter.ExePackageElementName, this.ConvertSuppressSignatureValidation },
                { Wix3Converter.MsiPackageElementName, this.ConvertSuppressSignatureValidation },
                { Wix3Converter.MspPackageElementName, this.ConvertSuppressSignatureValidation },
                { Wix3Converter.MsuPackageElementName, this.ConvertSuppressSignatureValidation },
                { Wix3Converter.PayloadElementName, this.ConvertSuppressSignatureValidation },
                { Wix3Converter.CustomActionElementName, this.ConvertCustomActionElement },
                { Wix3Converter.UtilPermissionExElementName, this.ConvertUtilPermissionExElement },
                { Wix3Converter.PropertyElementName, this.ConvertPropertyElement },
                { Wix3Converter.WixElementWithoutNamespaceName, this.ConvertElementWithoutNamespace },
                { Wix3Converter.IncludeElementWithoutNamespaceName, this.ConvertElementWithoutNamespace },
            };

            this.Messaging = messaging;

            this.IndentationAmount = indentationAmount;

            this.ErrorsAsWarnings = new HashSet<ConverterTestType>(this.YieldConverterTypes(errorsAsWarnings));

            this.IgnoreErrors = new HashSet<ConverterTestType>(this.YieldConverterTypes(ignoreErrors));
        }

        private int Errors { get; set; }

        private HashSet<ConverterTestType> ErrorsAsWarnings { get; set; }

        private HashSet<ConverterTestType> IgnoreErrors { get; set; }

        private IMessaging Messaging { get; }

        private int IndentationAmount { get; set; }

        private string SourceFile { get; set; }

        /// <summary>
        /// Convert a file.
        /// </summary>
        /// <param name="sourceFile">The file to convert.</param>
        /// <param name="saveConvertedFile">Option to save the converted errors that are found.</param>
        /// <returns>The number of errors found.</returns>
        public int ConvertFile(string sourceFile, bool saveConvertedFile)
        {
            XDocument document;

            // Set the instance info.
            this.Errors = 0;
            this.SourceFile = sourceFile;

            try
            {
                document = XDocument.Load(this.SourceFile, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (XmlException e)
            {
                this.OnError(ConverterTestType.XmlException, null, "The xml is invalid.  Detail: '{0}'", e.Message);

                return this.Errors;
            }

            this.ConvertDocument(document);

            // Fix errors if requested and necessary.
            if (saveConvertedFile && 0 < this.Errors)
            {
                try
                {
                    using (var writer = File.CreateText(this.SourceFile))
                    {
                        document.Save(writer, SaveOptions.DisableFormatting | SaveOptions.OmitDuplicateNamespaces);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    this.OnError(ConverterTestType.UnauthorizedAccessException, null, "Could not write to file.");
                }
            }

            return this.Errors;
        }

        /// <summary>
        /// Convert a document.
        /// </summary>
        /// <param name="document">The document to convert.</param>
        /// <returns>The number of errors found.</returns>
        public int ConvertDocument(XDocument document)
        {
            var declaration = document.Declaration;

            // Convert the declaration.
            if (null != declaration)
            {
                if (!String.Equals("utf-8", declaration.Encoding, StringComparison.OrdinalIgnoreCase))
                {
                    if (this.OnError(ConverterTestType.DeclarationEncodingWrong, document.Root, "The XML declaration encoding is not properly set to 'utf-8'."))
                    {
                        declaration.Encoding = "utf-8";
                    }
                }
            }
            else // missing declaration
            {
                if (this.OnError(ConverterTestType.DeclarationMissing, null, "This file is missing an XML declaration on the first line."))
                {
                    document.Declaration = new XDeclaration("1.0", "utf-8", null);
                    document.Root.AddBeforeSelf(new XText(XDocumentNewLine.ToString()));
                }
            }

            // Start converting the nodes at the top.
            this.ConvertNodes(document.Nodes(), 0);

            return this.Errors;
        }

        private void ConvertNodes(IEnumerable<XNode> nodes, int level)
        {
            // Note we operate on a copy of the node list since we may
            // remove some whitespace nodes during this processing.
            foreach (var node in nodes.ToList())
            {
                if (node is XText text)
                {
                    if (!String.IsNullOrWhiteSpace(text.Value))
                    {
                        text.Value = text.Value.Trim();
                    }
                    else if (node.NextNode is XCData cdata)
                    {
                        this.EnsurePrecedingWhitespaceRemoved(text, node, ConverterTestType.WhitespacePrecedingNodeWrong);
                    }
                    else if (node.NextNode is XElement element)
                    {
                        this.EnsurePrecedingWhitespaceCorrect(text, node, level, ConverterTestType.WhitespacePrecedingNodeWrong);
                    }
                    else if (node.NextNode is null) // this is the space before the close element
                    {
                        if (node.PreviousNode is null || node.PreviousNode is XCData)
                        {
                            this.EnsurePrecedingWhitespaceRemoved(text, node.Parent, ConverterTestType.WhitespacePrecedingEndElementWrong);
                        }
                        else if (level == 0) // root element's close tag
                        {
                            this.EnsurePrecedingWhitespaceCorrect(text, node, 0, ConverterTestType.WhitespacePrecedingEndElementWrong);
                        }
                        else
                        {
                            this.EnsurePrecedingWhitespaceCorrect(text, node, level - 1, ConverterTestType.WhitespacePrecedingEndElementWrong);
                        }
                    }
                }
                else if (node is XElement element)
                {
                    this.ConvertElement(element);

                    this.ConvertNodes(element.Nodes(), level + 1);
                }
            }
        }

        private void EnsurePrecedingWhitespaceCorrect(XText whitespace, XNode node, int level, ConverterTestType testType)
        {
            if (!Wix3Converter.LeadingWhitespaceValid(this.IndentationAmount, level, whitespace.Value))
            {
                var message = testType == ConverterTestType.WhitespacePrecedingEndElementWrong ? "The whitespace preceding this end element is incorrect." : "The whitespace preceding this node is incorrect.";

                if (this.OnError(testType, node, message))
                {
                    Wix3Converter.FixupWhitespace(this.IndentationAmount, level, whitespace);
                }
            }
        }

        private void EnsurePrecedingWhitespaceRemoved(XText whitespace, XNode node, ConverterTestType testType)
        {
            if (!String.IsNullOrEmpty(whitespace.Value) && whitespace.NodeType != XmlNodeType.CDATA)
            {
                var message = testType == ConverterTestType.WhitespacePrecedingEndElementWrong ? "The whitespace preceding this end element is incorrect." : "The whitespace preceding this node is incorrect.";

                if (this.OnError(testType, node, message))
                {
                    whitespace.Remove();
                }
            }
        }

        private void ConvertElement(XElement element)
        {
            // Gather any deprecated namespaces, then update this element tree based on those deprecations.
            var deprecatedToUpdatedNamespaces = new Dictionary<XNamespace, XNamespace>();

            foreach (var declaration in element.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                if (Wix3Converter.OldToNewNamespaceMapping.TryGetValue(declaration.Value, out var ns))
                {
                    if (this.OnError(ConverterTestType.XmlnsValueWrong, declaration, "The namespace '{0}' is out of date.  It must be '{1}'.", declaration.Value, ns.NamespaceName))
                    {
                        deprecatedToUpdatedNamespaces.Add(declaration.Value, ns);
                    }
                }
            }

            if (deprecatedToUpdatedNamespaces.Any())
            {
                Wix3Converter.UpdateElementsWithDeprecatedNamespaces(element.DescendantsAndSelf(), deprecatedToUpdatedNamespaces);
            }

            // Apply any specialized conversion actions.
            if (this.ConvertElementMapping.TryGetValue(element.Name, out var convert))
            {
                convert(element);
            }
        }

        private void ConvertColumnElement(XElement element)
        {
            var category = element.Attribute("Category");
            if (category != null)
            {
                var camelCaseValue = LowercaseFirstChar(category.Value);
                if (category.Value != camelCaseValue &&
                    this.OnError(ConverterTestType.ColumnCategoryCamelCase, element, "The CustomTable Category attribute contains an incorrectly cased '{0}' value. Lowercase the first character instead.", category.Name))
                {
                    category.Value = camelCaseValue;
                }
            }

            var modularization = element.Attribute("Modularize");
            if (modularization != null)
            {
                var camelCaseValue = LowercaseFirstChar(modularization.Value);
                if (category.Value != camelCaseValue &&
                    this.OnError(ConverterTestType.ColumnModularizeCamelCase, element, "The CustomTable Modularize attribute contains an incorrectly cased '{0}' value. Lowercase the first character instead.", modularization.Name))
                {
                    modularization.Value = camelCaseValue;
                }
            }
        }

        private void ConvertCustomTableElement(XElement element)
        {
            var bootstrapperApplicationData = element.Attribute("BootstrapperApplicationData");
            if (bootstrapperApplicationData != null
                && this.OnError(ConverterTestType.BootstrapperApplicationDataDeprecated, element, "The CustomTable element contains deprecated '{0}' attribute. Use the 'Unreal' attribute instead.", bootstrapperApplicationData.Name))
            {
                element.Add(new XAttribute("Unreal", bootstrapperApplicationData.Value));
                bootstrapperApplicationData.Remove();
            }
        }

        private void ConvertDirectoryElement(XElement element)
        {
            if (null == element.Attribute("Name"))
            {
                var attribute = element.Attribute("ShortName");
                if (null != attribute)
                {
                    var shortName = attribute.Value;
                    if (this.OnError(ConverterTestType.AssignDirectoryNameFromShortName, element, "The directory ShortName attribute is being renamed to Name since Name wasn't specified for value '{0}'", shortName))
                    {
                        element.Add(new XAttribute("Name", shortName));
                        attribute.Remove();
                    }
                }
            }
        }

        private void ConvertFileElement(XElement element)
        {
            if (null == element.Attribute("Id"))
            {
                var attribute = element.Attribute("Name");

                if (null == attribute)
                {
                    attribute = element.Attribute("Source");
                }

                if (null != attribute)
                {
                    var name = Path.GetFileName(attribute.Value);

                    if (this.OnError(ConverterTestType.AssignAnonymousFileId, element, "The file id is being updated to '{0}' to ensure it remains the same as the default", name))
                    {
                        IEnumerable<XAttribute> attributes = element.Attributes().ToList();
                        element.RemoveAttributes();
                        element.Add(new XAttribute("Id", GetIdentifierFromName(name)));
                        element.Add(attributes);
                    }
                }
            }
        }

        private void ConvertSuppressSignatureValidation(XElement element)
        {
            var suppressSignatureValidation = element.Attribute("SuppressSignatureValidation");

            if (null != suppressSignatureValidation)
            {
                if (this.OnError(ConverterTestType.SuppressSignatureValidationDeprecated, element, "The chain package element contains deprecated '{0}' attribute. Use the 'EnableSignatureValidation' attribute instead.", suppressSignatureValidation.Name))
                {
                    if ("no" == suppressSignatureValidation.Value)
                    {
                        element.Add(new XAttribute("EnableSignatureValidation", "yes"));
                    }
                }

                suppressSignatureValidation.Remove();
            }
        }

        private void ConvertCustomActionElement(XElement xCustomAction)
        {
            var xBinaryKey = xCustomAction.Attribute("BinaryKey");

            if (xBinaryKey?.Value == "WixCA" || xBinaryKey?.Value == "UtilCA")
            {
                if (this.OnError(ConverterTestType.WixCABinaryIdRenamed, xCustomAction, "The WixCA custom action DLL Binary table id has been renamed. Use the id 'Wix4UtilCA_X86' instead."))
                {
                    xBinaryKey.Value = "Wix4UtilCA_X86";
                }
            }

            if (xBinaryKey?.Value == "WixCA_x64" || xBinaryKey?.Value == "UtilCA_x64")
            {
                if (this.OnError(ConverterTestType.WixCABinaryIdRenamed, xCustomAction, "The WixCA_x64 custom action DLL Binary table id has been renamed. Use the id 'Wix4UtilCA_X64' instead."))
                {
                    xBinaryKey.Value = "Wix4UtilCA_X64";
                }
            }

            var xDllEntry = xCustomAction.Attribute("DllEntry");

            if (xDllEntry?.Value == "CAQuietExec" || xDllEntry?.Value == "CAQuietExec64")
            {
                if (this.OnError(ConverterTestType.QuietExecCustomActionsRenamed, xCustomAction, "The CAQuietExec and CAQuietExec64 custom action ids have been renamed. Use the ids 'WixQuietExec' and 'WixQuietExec64' instead."))
                {
                    xDllEntry.Value = xDllEntry.Value.Replace("CAQuietExec", "WixQuietExec");
                }
            }

            var xProperty = xCustomAction.Attribute("Property");

            if (xProperty?.Value == "QtExecCmdLine" || xProperty?.Value == "QtExec64CmdLine")
            {
                if (this.OnError(ConverterTestType.QuietExecCustomActionsRenamed, xCustomAction, "The QtExecCmdLine and QtExec64CmdLine property ids have been renamed. Use the ids 'WixQuietExecCmdLine' and 'WixQuietExec64CmdLine' instead."))
                {
                    xProperty.Value = xProperty.Value.Replace("QtExec", "WixQuietExec");
                }
            }
        }

        private void ConvertPropertyElement(XElement xProperty)
        {
            var xId = xProperty.Attribute("Id");

            if (xId.Value == "QtExecCmdTimeout")
            {
                this.OnError(ConverterTestType.QtExecCmdTimeoutAmbiguous, xProperty, "QtExecCmdTimeout was previously used for both CAQuietExec and CAQuietExec64. For WixQuietExec, use WixQuietExecCmdTimeout. For WixQuietExec64, use WixQuietExec64CmdTimeout.");
            }
        }

        private void ConvertUtilPermissionExElement(XElement element)
        {
            if (null == element.Attribute("Inheritable"))
            {
                var inheritable = element.Parent.Name == CreateFolderElementName;
                if (!inheritable)
                {
                    if (this.OnError(ConverterTestType.AssignPermissionExInheritable, element, "The PermissionEx Inheritable attribute is being set to 'no' to ensure it remains the same as the v3 default"))
                    {
                        element.Add(new XAttribute("Inheritable", "no"));
                    }
                }
            }
        }

        /// <summary>
        /// Converts a Wix element.
        /// </summary>
        /// <param name="element">The Wix element to convert.</param>
        /// <returns>The converted element.</returns>
        private void ConvertElementWithoutNamespace(XElement element)
        {
            if (this.OnError(ConverterTestType.XmlnsMissing, element, "The xmlns attribute is missing.  It must be present with a value of '{0}'.", WixNamespace.NamespaceName))
            {
                element.Name = WixNamespace.GetName(element.Name.LocalName);

                element.Add(new XAttribute("xmlns", WixNamespace.NamespaceName)); // set the default namespace.

                foreach (var elementWithoutNamespace in element.DescendantsAndSelf().Where(e => XNamespace.None == e.Name.Namespace))
                {
                    elementWithoutNamespace.Name = WixNamespace.GetName(elementWithoutNamespace.Name.LocalName);
                }
            }
        }

        private IEnumerable<ConverterTestType> YieldConverterTypes(IEnumerable<string> types)
        {
            if (null != types)
            {
                foreach (var type in types)
                {

                    if (Enum.TryParse<ConverterTestType>(type, true, out var itt))
                    {
                        yield return itt;
                    }
                    else // not a known ConverterTestType
                    {
                        this.OnError(ConverterTestType.ConverterTestTypeUnknown, null, "Unknown error type: '{0}'.", type);
                    }
                }
            }
        }

        private static void UpdateElementsWithDeprecatedNamespaces(IEnumerable<XElement> elements, Dictionary<XNamespace, XNamespace> deprecatedToUpdatedNamespaces)
        {
            foreach (var element in elements)
            {

                if (deprecatedToUpdatedNamespaces.TryGetValue(element.Name.Namespace, out var ns))
                {
                    element.Name = ns.GetName(element.Name.LocalName);
                }

                // Remove all the attributes and add them back to with their namespace updated (as necessary).
                IEnumerable<XAttribute> attributes = element.Attributes().ToList();
                element.RemoveAttributes();

                foreach (var attribute in attributes)
                {
                    var convertedAttribute = attribute;

                    if (attribute.IsNamespaceDeclaration)
                    {
                        if (deprecatedToUpdatedNamespaces.TryGetValue(attribute.Value, out ns))
                        {
                            convertedAttribute = ("xmlns" == attribute.Name.LocalName) ? new XAttribute(attribute.Name.LocalName, ns.NamespaceName) : new XAttribute(XNamespace.Xmlns + attribute.Name.LocalName, ns.NamespaceName);
                        }
                    }
                    else if (deprecatedToUpdatedNamespaces.TryGetValue(attribute.Name.Namespace, out ns))
                    {
                        convertedAttribute = new XAttribute(ns.GetName(attribute.Name.LocalName), attribute.Value);
                    }

                    element.Add(convertedAttribute);
                }
            }
        }

        /// <summary>
        /// Determine if the whitespace preceding a node is appropriate for its depth level.
        /// </summary>
        /// <param name="indentationAmount">Indentation value to use when validating leading whitespace.</param>
        /// <param name="level">The depth level that should match this whitespace.</param>
        /// <param name="whitespace">The whitespace to validate.</param>
        /// <returns>true if the whitespace is legal; false otherwise.</returns>
        private static bool LeadingWhitespaceValid(int indentationAmount, int level, string whitespace)
        {
            // Strip off leading newlines; there can be an arbitrary number of these.
            whitespace = whitespace.TrimStart(XDocumentNewLine);

            var indentation = new string(' ', level * indentationAmount);

            return whitespace == indentation;
        }

        /// <summary>
        /// Fix the whitespace in a whitespace node.
        /// </summary>
        /// <param name="indentationAmount">Indentation value to use when validating leading whitespace.</param>
        /// <param name="level">The depth level of the desired whitespace.</param>
        /// <param name="whitespace">The whitespace node to fix.</param>
        private static void FixupWhitespace(int indentationAmount, int level, XText whitespace)
        {
            var value = new StringBuilder(whitespace.Value.Length);

            // Keep any previous preceeding new lines.
            var newlines = whitespace.Value.TakeWhile(c => c == XDocumentNewLine).Count();

            // Ensure there is always at least one new line before the indentation.
            value.Append(XDocumentNewLine, newlines == 0 ? 1 : newlines);

            whitespace.Value = value.Append(' ', level * indentationAmount).ToString();
        }

        /// <summary>
        /// Output an error message to the console.
        /// </summary>
        /// <param name="converterTestType">The type of converter test.</param>
        /// <param name="node">The node that caused the error.</param>
        /// <param name="message">Detailed error message.</param>
        /// <param name="args">Additional formatted string arguments.</param>
        /// <returns>Returns true indicating that action should be taken on this error, and false if it should be ignored.</returns>
        private bool OnError(ConverterTestType converterTestType, XObject node, string message, params object[] args)
        {
            if (this.IgnoreErrors.Contains(converterTestType)) // ignore the error
            {
                return false;
            }

            // Increase the error count.
            this.Errors++;

            var sourceLine = (null == node) ? new SourceLineNumber(this.SourceFile ?? "wixcop.exe") : new SourceLineNumber(this.SourceFile, ((IXmlLineInfo)node).LineNumber);
            var warning = this.ErrorsAsWarnings.Contains(converterTestType);
            var display = String.Format(CultureInfo.CurrentCulture, message, args);

            var msg = new Message(sourceLine, warning ? MessageLevel.Warning : MessageLevel.Error, (int)converterTestType, "{0} ({1})", display, converterTestType.ToString());

            this.Messaging.Write(msg);

            return true;
        }

        /// <summary>
        /// Return an identifier based on passed file/directory name
        /// </summary>
        /// <param name="name">File/directory name to generate identifer from</param>
        /// <returns>A version of the name that is a legal identifier.</returns>
        /// <remarks>This is duplicated from WiX's Common class.</remarks>
        private static string GetIdentifierFromName(string name)
        {
            var result = IllegalIdentifierCharacters.Replace(name, "_"); // replace illegal characters with "_".

            // MSI identifiers must begin with an alphabetic character or an
            // underscore. Prefix all other values with an underscore.
            if (AddPrefix.IsMatch(name))
            {
                result = String.Concat("_", result);
            }

            return result;
        }

        private static string LowercaseFirstChar(string value)
        {
            if (!String.IsNullOrEmpty(value))
            {
                var c = Char.ToLowerInvariant(value[0]);
                if (c != value[0])
                {
                    var remainder = value.Length > 1 ? value.Substring(1) : String.Empty;
                    return c + remainder;
                }
            }

            return value;
        }

        /// <summary>
        /// Converter test types.  These are used to condition error messages down to warnings.
        /// </summary>
        private enum ConverterTestType
        {
            /// <summary>
            /// Internal-only: displayed when a string cannot be converted to an ConverterTestType.
            /// </summary>
            ConverterTestTypeUnknown,

            /// <summary>
            /// Displayed when an XML loading exception has occurred.
            /// </summary>
            XmlException,

            /// <summary>
            /// Displayed when a file cannot be accessed; typically when trying to save back a fixed file.
            /// </summary>
            UnauthorizedAccessException,

            /// <summary>
            /// Displayed when the encoding attribute in the XML declaration is not 'UTF-8'.
            /// </summary>
            DeclarationEncodingWrong,

            /// <summary>
            /// Displayed when the XML declaration is missing from the source file.
            /// </summary>
            DeclarationMissing,

            /// <summary>
            /// Displayed when the whitespace preceding a CDATA node is wrong.
            /// </summary>
            WhitespacePrecedingCDATAWrong,

            /// <summary>
            /// Displayed when the whitespace preceding a node is wrong.
            /// </summary>
            WhitespacePrecedingNodeWrong,

            /// <summary>
            /// Displayed when an element is not empty as it should be.
            /// </summary>
            NotEmptyElement,

            /// <summary>
            /// Displayed when the whitespace following a CDATA node is wrong.
            /// </summary>
            WhitespaceFollowingCDATAWrong,

            /// <summary>
            /// Displayed when the whitespace preceding an end element is wrong.
            /// </summary>
            WhitespacePrecedingEndElementWrong,

            /// <summary>
            /// Displayed when the xmlns attribute is missing from the document element.
            /// </summary>
            XmlnsMissing,

            /// <summary>
            /// Displayed when the xmlns attribute on the document element is wrong.
            /// </summary>
            XmlnsValueWrong,

            /// <summary>
            /// Assign an identifier to a File element when on Id attribute is specified.
            /// </summary>
            AssignAnonymousFileId,

            /// <summary>
            /// SuppressSignatureValidation attribute is deprecated and replaced with EnableSignatureValidation.
            /// </summary>
            SuppressSignatureValidationDeprecated,

            /// <summary>
            /// WixCA Binary/@Id has been renamed to UtilCA.
            /// </summary>
            WixCABinaryIdRenamed,

            /// <summary>
            /// QtExec custom actions have been renamed.
            /// </summary>
            QuietExecCustomActionsRenamed,

            /// <summary>
            /// QtExecCmdTimeout was previously used for both CAQuietExec and CAQuietExec64. For WixQuietExec, use WixQuietExecCmdTimeout. For WixQuietExec64, use WixQuietExec64CmdTimeout.
            /// </summary>
            QtExecCmdTimeoutAmbiguous,

            /// <summary>
            /// Directory/@ShortName may only be specified with Directory/@Name.
            /// </summary>
            AssignDirectoryNameFromShortName,

            /// <summary>
            /// BootstrapperApplicationData attribute is deprecated and replaced with Unreal.
            /// </summary>
            BootstrapperApplicationDataDeprecated,

            /// <summary>
            /// Inheritable is new and is now defaulted to 'yes' which is a change in behavior for all but children of CreateFolder.
            /// </summary>
            AssignPermissionExInheritable,

            /// <summary>
            /// Column element's Category attribute is camel-case.
            /// </summary>
            ColumnCategoryCamelCase,

            /// <summary>
            /// Column element's Modularize attribute is camel-case.
            /// </summary>
            ColumnModularizeCamelCase,
        }
    }
}
