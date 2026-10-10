// The default theme without the font it ships with: the site uses the app's own font stack.
import type { Theme } from 'vitepress'
import DefaultTheme from 'vitepress/theme-without-fonts'
import MermaidDiagram from './MermaidDiagram.vue'
import './custom.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('MermaidDiagram', MermaidDiagram)
  },
} satisfies Theme
