import js from '@eslint/js';
import globals from 'globals';

export default [
  { ignores: ['**/node_modules/**', '**/bin/**', '**/obj/**'] },
  js.configs.recommended,
  {
    files: ['src/AgentAquarium/wwwroot/**/*.js'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: globals.browser,
    },
    rules: {
      eqeqeq: ['error', 'always'],
      'no-var': 'error',
      'prefer-const': 'error',
      'no-shadow': 'error',
      'no-implicit-globals': 'error',
      'no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
    },
  },
  {
    // Page scripts that .claude/skills/docs-media/capture.sh evals in the browser: classic scripts.
    files: ['.claude/skills/**/*.js'],
    languageOptions: { ecmaVersion: 'latest', sourceType: 'script', globals: globals.browser },
  },
  {
    files: ['*.config.js'],
    languageOptions: { sourceType: 'module', globals: globals.node },
  },
];
