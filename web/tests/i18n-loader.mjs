import fs from 'node:fs/promises'
import ts from 'typescript'

export function transpile(source) {
  return ts.transpileModule(source, {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 },
  }).outputText
}
const dataUrl = (source) => `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`
const englishUrl = dataUrl(
  transpile(await fs.readFile(new URL('../src/locales/en.ts', import.meta.url), 'utf8')),
)
const i18nSource = transpile(await fs.readFile(new URL('../src/i18n.ts', import.meta.url), 'utf8'))
  .replace("'./locales/en'", JSON.stringify(englishUrl))
  .replace("'vue'", JSON.stringify(import.meta.resolve('vue')))
export const i18nUrl = dataUrl(i18nSource)
export const withI18n = (source) => source.replace(/from ['"]\.\.?\/i18n['"]/g, `from '${i18nUrl}'`)

const timeUrl = dataUrl(
  withI18n(transpile(await fs.readFile(new URL('../src/time.ts', import.meta.url), 'utf8'))),
)
export const evidencePresentationUrl = dataUrl(
  withI18n(
    transpile(
      await fs.readFile(new URL('../src/evidencePresentation.ts', import.meta.url), 'utf8'),
    ),
  ).replace(/from ['"]\.\/time['"]/g, `from '${timeUrl}'`),
)
