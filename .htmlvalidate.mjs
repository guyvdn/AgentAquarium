import { defineConfig } from 'html-validate';

export default defineConfig({
  extends: ['html-validate:recommended'],
  rules: {
    // Prettier owns markup style: it writes `<!doctype html>` in lowercase and closes void elements (`<br />`).
    'doctype-style': 'off',
    'void-style': ['error', { style: 'selfclose' }],
  },
});
