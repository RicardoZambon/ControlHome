import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import App from './App.vue'

describe('App', () => {
  it('renders the application name', () => {
    const wrapper = mount(App)
    expect(wrapper.text()).toContain('ControlHome')
  })

  it('renders a design-system button', () => {
    const wrapper = mount(App)
    expect(wrapper.find('[data-slot="button"]').exists()).toBe(true)
  })
})
