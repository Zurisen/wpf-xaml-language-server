// Launches VS Code with the extension and the samples folder, and runs test/suite.ts inside it.
// The server must be built (src/WpfXaml.LanguageServer) and the samples built/restored first.
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
  const extensionDevelopmentPath = path.resolve(__dirname, "..");
  const extensionTestsPath = path.resolve(__dirname, "suite.js");
  const repo = path.resolve(extensionDevelopmentPath, "..", "..");
  const samples = path.join(repo, "samples");
  const serverDirectory = process.env.WPF_XAML_LS_DIR ?? path.join(repo, "src", "WpfXaml.LanguageServer", "bin", "Debug", "net10.0");
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), "wpf-xaml-ls-vscode-"));

  await runTests({
    // Use an installed VS Code when given (local runs); CI downloads one.
    vscodeExecutablePath: process.env.VSCODE_EXECUTABLE || undefined,
    extensionDevelopmentPath,
    extensionTestsPath,
    launchArgs: [samples, "--disable-extensions", "--user-data-dir", userDataDir, "--skip-welcome", "--skip-release-notes"],
    // The extension's default serverPath ("wpf-xaml-ls") must find the built server on PATH.
    extensionTestsEnv: { PATH: `${serverDirectory}${path.delimiter}${process.env.PATH ?? ""}` },
  });
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
