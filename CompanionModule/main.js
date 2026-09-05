import {
  InstanceBase,
  InstanceStatus,
  Regex,
  runEntrypoint,
} from '@companion-module/base'

class ATLiveOverlayInstance extends InstanceBase {
  async init(config) {
    this.config = config
    this.statusData = null
    this.updateStatus(InstanceStatus.Connecting)
    this.initActions()
    this.initFeedbacks()
    this.initVariables()
    this.initPresets()
    await this.pollStatus()
    this.pollTimer = setInterval(() => this.pollStatus(), 2000)
  }

  async destroy() {
    clearInterval(this.pollTimer)
  }

  async configUpdated(config) {
    this.config = config
    await this.pollStatus()
  }

  getConfigFields() {
    return [
      {
        type: 'textinput',
        id: 'host',
        label: 'AT LiveOverlay computer IP / hostname',
        width: 8,
        default: '127.0.0.1',
        regex: Regex.HOSTNAME,
      },
      {
        type: 'number',
        id: 'port',
        label: 'HTTP port',
        width: 4,
        default: 8765,
        min: 1,
        max: 65535,
      },
      {
        type: 'secret-text',
        id: 'token',
        label: 'API token (from AT LiveOverlay > Remote control)',
        width: 12,
        default: '',
      },
    ]
  }

  baseUrl() {
    return `http://${this.config.host || '127.0.0.1'}:${this.config.port || 8765}`
  }

  async request(path) {
    const separator = path.includes('?') ? '&' : '?'
    const url = `${this.baseUrl()}${path}${separator}token=${encodeURIComponent(this.config.token || '')}`
    const response = await fetch(url, { method: 'GET' })
    if (!response.ok) {
      const error = new Error(`HTTP ${response.status}`)
      error.status = response.status
      throw error
    }
    return response
  }

  async pollStatus() {
    try {
      const response = await this.request('/status')
      const data = await response.json()
      this.statusData = data
      this.updateStatus(InstanceStatus.Ok)

      const overlays = Array.isArray(data.overlays) ? data.overlays : []
      const definitions = [
        { variableId: 'version', name: 'AT LiveOverlay version' },
        { variableId: 'build', name: 'AT LiveOverlay build' },
        { variableId: 'overlay_count', name: 'Overlay count' },
        { variableId: 'active_scene', name: 'Active scene' },
      ]
      const values = {
        version: data.version || '',
        build: data.build || '',
        overlay_count: overlays.length,
        active_scene: data.activeScene || '',
      }

      for (const overlay of overlays) {
        const id = overlay.Id
        definitions.push(
          { variableId: `overlay_${id}_name`, name: `Overlay ${id} name` },
          { variableId: `overlay_${id}_url`, name: `Overlay ${id} URL` },
          { variableId: `overlay_${id}_visible`, name: `Overlay ${id} visible` },
          { variableId: `overlay_${id}_locked`, name: `Overlay ${id} locked` },
          { variableId: `overlay_${id}_opacity`, name: `Overlay ${id} opacity %` },
          { variableId: `overlay_${id}_refresh`, name: `Overlay ${id} refresh seconds` },
        )
        values[`overlay_${id}_name`] = overlay.Name || ''
        values[`overlay_${id}_url`] = overlay.Url || ''
        values[`overlay_${id}_visible`] = overlay.visible ? 'yes' : 'no'
        values[`overlay_${id}_locked`] = overlay.locked ? 'yes' : 'no'
        values[`overlay_${id}_opacity`] = overlay.opacity ?? ''
        values[`overlay_${id}_refresh`] = overlay.refreshSeconds ?? ''
      }

      this.setVariableDefinitions(definitions)
      this.setVariableValues(values)
      this.checkFeedbacks()
    } catch (error) {
      this.statusData = null
      if (error.status === 401) {
        this.updateStatus(InstanceStatus.AuthenticationFailure, 'Invalid API token - copy it again from AT LiveOverlay > Remote control')
      } else {
        this.updateStatus(InstanceStatus.ConnectionFailure, error.message)
      }
      this.checkFeedbacks()
    }
  }

  overlayOption() {
    return { type: 'number', id: 'id', label: 'Overlay ID', default: 1, min: 1, max: 999 }
  }

  initActions() {
    const simple = (name, command) => ({
      name,
      options: [this.overlayOption()],
      callback: async (event) => this.request(`/overlay/${event.options.id}/${command}`),
    })

    this.setActionDefinitions({
      show: simple('Show overlay', 'show'),
      hide: simple('Hide overlay', 'hide'),
      reload: simple('Reload overlay', 'reload'),
      edit: simple('Edit / move overlay', 'edit'),
      live: simple('Return overlay to live mode', 'live'),
      lock: simple('Lock overlay (click-through)', 'lock'),
      unlock: simple('Unlock overlay', 'unlock'),
      close: simple('Close overlay', 'close'),
      set_url: {
        name: 'Set overlay URL',
        options: [this.overlayOption(), { type: 'textinput', id: 'url', label: 'URL', default: 'http://10.100.70.101:4007/timer', useVariables: true }],
        callback: async (event) => {
          const url = await this.parseVariablesInString(event.options.url)
          return this.request(`/overlay/${event.options.id}/seturl?url=${encodeURIComponent(url)}`)
        },
      },
      opacity: {
        name: 'Set overlay opacity',
        options: [this.overlayOption(), { type: 'number', id: 'value', label: 'Opacity %', default: 100, min: 20, max: 100 }],
        callback: async (event) => this.request(`/overlay/${event.options.id}/opacity?value=${event.options.value}`),
      },
      refresh: {
        name: 'Set auto-refresh seconds',
        options: [this.overlayOption(), { type: 'number', id: 'seconds', label: 'Seconds (0 disables)', default: 0, min: 0, max: 3600 }],
        callback: async (event) => this.request(`/overlay/${event.options.id}/refresh?seconds=${event.options.seconds}`),
      },
      create: {
        name: 'Create overlay',
        options: [{ type: 'textinput', id: 'url', label: 'URL', default: 'http://10.100.70.101:4007/timer', useVariables: true }],
        callback: async (event) => {
          const url = await this.parseVariablesInString(event.options.url)
          return this.request(`/overlay/create?url=${encodeURIComponent(url)}`)
        },
      },
      show_all: { name: 'Show all overlays', options: [], callback: async () => this.request('/overlay/all/show') },
      hide_all: { name: 'Hide all overlays', options: [], callback: async () => this.request('/overlay/all/hide') },
      reload_all: { name: 'Reload all overlays', options: [], callback: async () => this.request('/overlay/all/reload') },
      load_scene: {
        name: 'Load scene',
        options: [{ type: 'textinput', id: 'name', label: 'Scene name', default: '', useVariables: true }],
        callback: async (event) => {
          const name = await this.parseVariablesInString(event.options.name)
          return this.request(`/scene/load?name=${encodeURIComponent(name)}`)
        },
      },
    })
  }

  initFeedbacks() {
    this.setFeedbackDefinitions({
      visible: {
        name: 'Overlay is visible',
        type: 'boolean',
        defaultStyle: { bgcolor: 0x00aa00, color: 0xffffff },
        options: [this.overlayOption()],
        callback: (feedback) => Boolean(this.statusData?.overlays?.find((o) => o.Id === feedback.options.id)?.visible),
      },
      locked: {
        name: 'Overlay is locked',
        type: 'boolean',
        defaultStyle: { bgcolor: 0xee7c19, color: 0x000000 },
        options: [this.overlayOption()],
        callback: (feedback) => Boolean(this.statusData?.overlays?.find((o) => o.Id === feedback.options.id)?.locked),
      },
      exists: {
        name: 'Overlay exists',
        type: 'boolean',
        defaultStyle: { bgcolor: 0x333333, color: 0xffffff },
        options: [this.overlayOption()],
        callback: (feedback) => Boolean(this.statusData?.overlays?.find((o) => o.Id === feedback.options.id)),
      },
      scene_active: {
        name: 'Scene is active',
        type: 'boolean',
        defaultStyle: { bgcolor: 0x0066cc, color: 0xffffff },
        options: [{ type: 'textinput', id: 'name', label: 'Scene name', default: '', useVariables: true }],
        callback: async (feedback) => {
          const name = await this.parseVariablesInString(feedback.options.name)
          return Boolean(name) && this.statusData?.activeScene === name
        },
      },
    })
  }

  initVariables() {
    this.setVariableDefinitions([
      { variableId: 'version', name: 'AT LiveOverlay version' },
      { variableId: 'build', name: 'AT LiveOverlay build' },
      { variableId: 'overlay_count', name: 'Overlay count' },
      { variableId: 'active_scene', name: 'Active scene' },
    ])
  }

  initPresets() {
    const style = { text: '$(this:action)', size: 'auto', color: 0xffffff, bgcolor: 0x222222 }
    this.setPresetDefinitions({
      show: {
        type: 'button', category: 'Overlay 1', name: 'Show overlay 1',
        style: { ...style, text: 'SHOW\nOVERLAY' },
        steps: [{ down: [{ actionId: 'show', options: { id: 1 } }], up: [] }],
        feedbacks: [{ feedbackId: 'visible', options: { id: 1 } }],
      },
      hide: {
        type: 'button', category: 'Overlay 1', name: 'Hide overlay 1',
        style: { ...style, text: 'HIDE\nOVERLAY' },
        steps: [{ down: [{ actionId: 'hide', options: { id: 1 } }], up: [] }],
        feedbacks: [],
      },
      reload: {
        type: 'button', category: 'Overlay 1', name: 'Reload overlay 1',
        style: { ...style, text: 'RELOAD\nOVERLAY' },
        steps: [{ down: [{ actionId: 'reload', options: { id: 1 } }], up: [] }],
        feedbacks: [{ feedbackId: 'exists', options: { id: 1 } }],
      },
      lock: {
        type: 'button', category: 'Overlay 1', name: 'Lock overlay 1',
        style: { ...style, text: 'LOCK\nOVERLAY' },
        steps: [{ down: [{ actionId: 'lock', options: { id: 1 } }], up: [] }],
        feedbacks: [{ feedbackId: 'locked', options: { id: 1 } }],
      },
      show_all: {
        type: 'button', category: 'All overlays', name: 'Show all overlays',
        style: { ...style, text: 'SHOW\nALL' },
        steps: [{ down: [{ actionId: 'show_all', options: {} }], up: [] }],
        feedbacks: [],
      },
      hide_all: {
        type: 'button', category: 'All overlays', name: 'Hide all overlays',
        style: { ...style, text: 'HIDE\nALL' },
        steps: [{ down: [{ actionId: 'hide_all', options: {} }], up: [] }],
        feedbacks: [],
      },
      reload_all: {
        type: 'button', category: 'All overlays', name: 'Reload all overlays',
        style: { ...style, text: 'RELOAD\nALL' },
        steps: [{ down: [{ actionId: 'reload_all', options: {} }], up: [] }],
        feedbacks: [],
      },
      load_scene_example: {
        type: 'button', category: 'Scenes', name: 'Load scene (edit the name)',
        style: { ...style, text: 'LOAD\nSCENE' },
        steps: [{ down: [{ actionId: 'load_scene', options: { name: 'My scene' } }], up: [] }],
        feedbacks: [{ feedbackId: 'scene_active', options: { name: 'My scene' } }],
      },
    })
  }
}

runEntrypoint(ATLiveOverlayInstance, [])
