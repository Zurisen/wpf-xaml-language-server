-- wpf-xaml-ls for Neovim 0.11+ (uses vim.lsp.config / vim.lsp.enable).
-- Put this file in your config (e.g. ~/.config/nvim/after/plugin/wpf-xaml-ls.lua) or copy its contents.
-- Requires the server on PATH: `dotnet tool install -g WpfXaml.LanguageServer --prerelease`.

-- Neovim has no built-in "xaml" filetype; give .xaml its own filetype and reuse the XML tree-sitter parser.
vim.filetype.add({ extension = { xaml = 'xaml' } })
pcall(vim.treesitter.language.register, 'xml', 'xaml')

vim.lsp.config('wpf_xaml_ls', {
  cmd = { 'wpf-xaml-ls' },
  filetypes = { 'xaml' },
  -- The nearest folder with a solution wins; fall back to a project file, then to git.
  root_dir = function(bufnr, on_dir)
    local function has(pattern)
      return function(name) return name:match(pattern) ~= nil end
    end
    on_dir(vim.fs.root(bufnr, has('%.slnx?$')) or vim.fs.root(bufnr, has('%.csproj$')) or vim.fs.root(bufnr, '.git'))
  end,
  init_options = {
    -- solution = 'path/to/App.sln', -- load this instead of discovering one under the root
    -- loadProjects = false,          -- framework types only (no MSBuild)
    -- frameworkVersion = 8,          -- WPF targeting pack used before/without a project
  },
})

vim.lsp.enable('wpf_xaml_ls')
