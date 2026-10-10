<script lang="ts">
let drawn = 0
</script>

<script setup lang="ts">
// One diagram. The page is built with the diagram's source as text, and that is what a browser
// without scripts shows. With scripts, Mermaid is fetched on the first page that has a diagram,
// from this site, and the drawing replaces the text.
import { computed, onMounted, ref, watch } from 'vue'
import { useData } from 'vitepress'

const props = defineProps<{ source: string }>()
const { isDark } = useData()
const text = computed(() => decodeURIComponent(props.source))
const drawing = ref('')
const width = ref('0px')

async function draw(): Promise<void> {
  try {
    const { default: mermaid } = await import('mermaid')
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: isDark.value ? 'dark' : 'default',
    })
    drawn += 1
    const { svg } = await mermaid.render(`diagram-${drawn}`, text.value)
    // Mermaid writes the drawing's own width as its largest width.
    width.value = /max-width:\s*([\d.]+px)/.exec(svg)?.[1] ?? '0px'
    drawing.value = svg
  } catch (error) {
    console.error(error)
  }
}

onMounted(draw)
watch(isDark, draw)
</script>

<template>
  <div v-if="drawing" class="diagram" :style="{ '--diagram-width': width }" v-html="drawing"></div>
  <pre v-else class="diagram-source"><code>{{ text }}</code></pre>
</template>
