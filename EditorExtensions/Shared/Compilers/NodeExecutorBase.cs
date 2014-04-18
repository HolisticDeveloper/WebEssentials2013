﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Helpers;

namespace MadsKristensen.EditorExtensions
{
    public abstract class NodeExecutorBase
    {
        protected static readonly string WebEssentialsResourceDirectory = Path.Combine(Path.GetDirectoryName(typeof(NodeExecutorBase).Assembly.Location), @"Resources");
        private static readonly string NodePath = Path.Combine(WebEssentialsResourceDirectory, @"nodejs\node.exe");

        protected string MapFileName { get; set; }
        protected abstract string CompilerPath { get; }
        protected virtual Regex ErrorParsingPattern { get { return null; } }
        protected virtual Func<string, IEnumerable<CompilerError>> ParseErrors { get { return ParseErrorsWithRegex; } }

        ///<summary>Indicates whether this compiler will emit a source map file.  Will only return true if aupported and enabled in user settings.</summary>
        public abstract bool GenerateSourceMap { get; }
        public abstract string TargetExtension { get; }
        public abstract string ServiceName { get; }
        ///<summary>Indicates whether this compiler is capable of compiling to a filename that doesn't match the source filename.</summary>
        public virtual bool RequireMatchingFileName { get { return false; } }

        public async Task<CompilerResult> CompileAsync(string sourceFileName, string targetFileName)
        {
            if (RequireMatchingFileName &&
                Path.GetFileName(targetFileName) != Path.GetFileNameWithoutExtension(sourceFileName) + TargetExtension &&
                Path.GetFileName(targetFileName) != Path.GetFileNameWithoutExtension(sourceFileName) + ".min" + TargetExtension)
                throw new ArgumentException(ServiceName + " cannot compile to a targetFileName with a different name.  Only the containing directory can be different.", "targetFileName");

            var scriptArgs = GetArguments(sourceFileName, targetFileName);

            var errorOutputFile = Path.GetTempFileName();

            var cmdArgs = string.Format("\"{0}\" \"{1}\"", NodePath, CompilerPath);

            cmdArgs = string.Format("/c \"{0} {1} > \"{2}\" 2>&1\"", cmdArgs, scriptArgs, errorOutputFile);

            ProcessStartInfo start = new ProcessStartInfo("cmd")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(sourceFileName),
                Arguments = cmdArgs,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            try
            {
                ProjectHelpers.CheckOutFileFromSourceControl(targetFileName);

                MapFileName = MapFileName ?? targetFileName + ".map";

                if (GenerateSourceMap)
                    ProjectHelpers.CheckOutFileFromSourceControl(MapFileName);

                using (var process = await start.ExecuteAsync())
                {
                    if (targetFileName != null)
                        await MoveOutputContentToCorrectTarget(targetFileName);

                    return await ProcessResult(
                                     process,
                                     (await FileHelpers.ReadAllTextRetry(errorOutputFile)).Trim(),
                                     sourceFileName,
                                     targetFileName,
                                     MapFileName
                                 );
                }
            }
            finally
            {
                File.Delete(errorOutputFile);

                if (!GenerateSourceMap)
                    File.Delete(MapFileName);
            }
        }

        private async Task<CompilerResult> ProcessResult(Process process, string errorText, string sourceFileName, string targetFileName, string mapFileName)
        {
            var result = await ValidateResult(process, targetFileName, errorText);
            var resultText = result.Result;
            bool success = result.IsSuccess;

            if (success)
            {
                var renewedResult = await PostProcessResult(resultText, sourceFileName, targetFileName);

                if (!ReferenceEquals(resultText, renewedResult))
                {
                    await FileHelpers.WriteAllTextRetry(targetFileName, renewedResult);
                    resultText = renewedResult;
                }
            }

            IEnumerable<CompilerError> errors = result.Errors;

            var compilerResult = await CompilerResultFactory.GenerateResult(
                                           sourceFileName: sourceFileName,
                                           targetFileName: targetFileName,
                                           mapFileName: mapFileName,
                                           isSuccess: success,
                                           result: resultText,
                                           errors: errors
                                       ) as CompilerResult;

            if (!success)
            {
                Logger.Log(ServiceName + ": " + Path.GetFileName(sourceFileName)
                         + " compilation failed: " + compilerResult.Errors.Select(e => e.Message).FirstOrDefault());
            }

            return compilerResult;
        }

        private async Task<dynamic> ValidateResult(Process process, string outputFile, string errorText)
        {
            string result = null;
            var isSuccess = false;
            IEnumerable<CompilerError> errors = null;

            try
            {
                if (process.ExitCode == 0)
                {
                    if (!string.IsNullOrEmpty(outputFile) && File.Exists(outputFile))
                        result = await FileHelpers.ReadAllTextRetry(outputFile);
                    isSuccess = true;
                }
                else
                {
                    errors = ParseErrors(errorText);
                }
            }
            catch (FileNotFoundException missingFileException)
            {
                Logger.Log(ServiceName + ": " + Path.GetFileName(outputFile) + " compilation failed. " + missingFileException.Message);
            }

            return new
            {
                Result = result,
                IsSuccess = isSuccess,
                Errors = errors
            };
        }

        protected IEnumerable<CompilerError> ParseErrorsWithJson(string error)
        {
            if (string.IsNullOrEmpty(error))
                return null;

            try
            {
                CompilerError[] results = Json.Decode<CompilerError[]>(error);

                if (results.Length == 0)
                    Logger.Log(ServiceName + " parse error: " + error);

                return results;
            }
            catch (ArgumentException)
            {
                Logger.Log(ServiceName + " parse error: " + error);
                return new[] { new CompilerError() { Message = error } };
            }
        }

        protected IEnumerable<CompilerError> ParseErrorsWithRegex(string error)
        {
            var matches = ErrorParsingPattern.Matches(error);

            if (matches.Count == 0)
            {
                Logger.Log(ServiceName + ": unparsable compilation error: " + error);
                return new[] { new CompilerError { Message = error } };
            }
            return matches.Cast<Match>().Select(match => new CompilerError
            {
                FileName = match.Groups["fileName"].Value,
                Message = match.Groups["message"].Value,
                Column = string.IsNullOrEmpty(match.Groups["column"].Value) ? 1 : int.Parse(match.Groups["column"].Value, CultureInfo.CurrentCulture),
                Line = int.Parse(match.Groups["line"].Value, CultureInfo.CurrentCulture)
            });
        }

        /// <summary>
        ///  In case of CoffeeScript, the compiler doesn't take output file path argument,
        ///  instead takes path to output directory. This method can be overridden by any
        ///  such compiler to move data to correct target.
        /// </summary>
        protected virtual Task MoveOutputContentToCorrectTarget(string targetFileName)
        {
            return Task.FromResult(0);
        }

        protected abstract string GetArguments(string sourceFileName, string targetFileName);

        protected abstract Task<string> PostProcessResult(string resultSource, string sourceFileName, string targetFileName);
    }
}
