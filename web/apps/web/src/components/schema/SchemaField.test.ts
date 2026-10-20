import type { ValueSchema } from '@controlhome/api-client'
import { Select } from '@controlhome/ui'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { createAppI18n, type AppLocale } from '../../i18n'
import SchemaField from './SchemaField.vue'
import SchemaForm from './SchemaForm.vue'

function field(schema: ValueSchema, props: Record<string, unknown> = {}, locale: AppLocale = 'en') {
  return mount(SchemaField, {
    props: { schema, label: 'Value', ...props },
    attachTo: document.body,
    global: { plugins: [createAppI18n({ locale })] },
  })
}

afterEach(() => {
  document.body.innerHTML = ''
})

describe('SchemaField renders every ValueSchema type', () => {
  it('bool: a switch that commits on toggle', async () => {
    const wrapper = field({ type: 'bool' }, { modelValue: false })
    const toggle = wrapper.get('button[role="switch"]')
    expect(toggle.attributes('aria-checked')).toBe('false')
    expect(wrapper.get('label').attributes('for')).toBe(toggle.attributes('id'))
    await toggle.trigger('click')
    expect(wrapper.emitted('update:modelValue')).toEqual([[true]])
    expect(wrapper.emitted('commit')).toEqual([[true]])
  })

  it('number: a decimal field (comma or dot) with its unit, and a slider for a bounded range', async () => {
    const wrapper = field({ type: 'number', min: 16, max: 30, step: 0.5, unit: '°C' }, { modelValue: 22 })
    const input = wrapper.get('input')
    expect(input.element.value).toBe('22')
    expect(wrapper.text()).toContain('°C')
    expect(wrapper.find('[data-slot="slider"]').exists()).toBe(true)
    input.element.value = '22,5'
    await input.trigger('input')
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual([22.5])
    expect(wrapper.emitted('commit')).toBeUndefined()
    await input.trigger('change')
    expect(wrapper.emitted('commit')).toEqual([[22.5]])
  })

  it('number: no slider without bounds; garbage is passed on for validation', async () => {
    const wrapper = field({ type: 'number', min: 0, integer: true })
    expect(wrapper.find('[data-slot="slider"]').exists()).toBe(false)
    const input = wrapper.get('input')
    input.element.value = 'abc'
    await input.trigger('input')
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['abc'])
  })

  it('enum: a select of the allowed values that commits the choice', async () => {
    const wrapper = field(
      { type: 'enum', values: ['off', 'cool', 'heat'] },
      { modelValue: 'cool', constraint: { values: ['off', 'cool'] }, optionLabel: (v: string) => `<${v}>` },
    )
    const select = wrapper.findComponent(Select)
    expect(select.props('modelValue')).toBe('cool')
    expect(wrapper.get('button[role="combobox"]').attributes('id')).toBe(
      wrapper.get('label').attributes('for'),
    )
    select.vm.$emit('update:modelValue', 'off')
    await flushPromises()
    expect(wrapper.emitted('update:modelValue')).toEqual([['off']])
    expect(wrapper.emitted('commit')).toEqual([['off']])
  })

  it('string: a text field (password when secret) that commits on change and Enter', async () => {
    const wrapper = field({ type: 'string', maxLength: 20 }, { modelValue: 'HDMI 1' })
    const input = wrapper.get('input')
    expect(input.attributes('type')).toBe('text')
    expect(input.attributes('maxlength')).toBe('20')
    // Typing updates the value; the change is committed only when the field is left or Enter is pressed.
    input.element.value = 'HDMI 2'
    await input.trigger('input')
    expect(wrapper.emitted('update:modelValue')).toEqual([['HDMI 2']])
    expect(wrapper.emitted('commit')).toBeUndefined()
    await wrapper.setProps({ modelValue: 'HDMI 2' })
    await input.trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('commit')).toEqual([['HDMI 2']])

    const secret = field({ type: 'string' }, { secret: true })
    expect(secret.get('input').attributes('type')).toBe('password')
    expect(secret.get('input').attributes('autocomplete')).toBe('new-password')
  })

  it('flags: one checkbox per flag, in the schema order', async () => {
    const wrapper = field({ type: 'flags', flags: ['eco', 'quiet', 'child'] }, { modelValue: ['child'] })
    const boxes = wrapper.findAll('button[role="checkbox"]')
    expect(boxes).toHaveLength(3)
    expect(wrapper.get('legend').text()).toBe('Value')
    await boxes[0]!.trigger('click')
    expect(wrapper.emitted('commit')).toEqual([[['eco', 'child']]])
  })

  it('json: read-only, formatted', () => {
    const wrapper = field({ type: 'json' }, { modelValue: { zones: [1, 2] } })
    expect(wrapper.get('pre').text()).toBe(JSON.stringify({ zones: [1, 2] }, null, 2))
    expect(wrapper.find('input').exists()).toBe(false)
  })

  it('momentary: the last event and the events it sends, read-only', () => {
    const wrapper = field(
      { type: 'momentary', events: ['single', 'double', 'long'] },
      { modelValue: 'double', optionLabel: (v: string) => `${v} press` },
    )
    expect(wrapper.get('[data-slot="schema-value"]').text()).toBe('double press')
    expect(wrapper.findAll('[data-slot="badge"]').map((badge) => badge.text())).toEqual([
      'single press',
      'double press',
      'long press',
    ])
  })

  it('read-only values are text with their unit, in the UI language', () => {
    expect(
      field({ type: 'number', unit: '°C' }, { modelValue: 21.5, readonly: true }, 'pt-BR')
        .get('[data-slot="schema-value"]')
        .text(),
    ).toBe('21,5 °C')
    expect(
      field({ type: 'bool' }, { modelValue: true, readonly: true }).get('button[role="switch"]').attributes(),
    ).toHaveProperty('disabled')
    expect(field({ type: 'string' }, { readonly: true }).get('[data-slot="schema-value"]').text()).toBe('—')
  })

  it('shows an error and marks the control invalid', () => {
    const wrapper = field({ type: 'string' }, { error: 'Too long' })
    const input = wrapper.get('input')
    expect(input.attributes('aria-invalid')).toBe('true')
    const message = wrapper.get('[role="alert"]')
    expect(message.text()).toBe('Too long')
    expect(input.attributes('aria-describedby')).toContain(message.attributes('id'))
  })
})

describe('SchemaForm', () => {
  it('validates like the server before submitting, then emits the values', async () => {
    const fields = [
      {
        key: 'name',
        label: 'Name',
        schema: { type: 'string', maxLength: 10 } as ValueSchema,
        required: true,
      },
      { key: 'seed', label: 'Seed', schema: { type: 'number', min: 0, integer: true } as ValueSchema },
    ]
    const wrapper = mount(SchemaForm, {
      props: { fields, modelValue: { name: '', seed: -2 } },
      attachTo: document.body,
      global: { plugins: [createAppI18n({ locale: 'en' })] },
    })
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('submit')).toBeUndefined()
    expect(wrapper.findAll('[role="alert"]').map((alert) => alert.text())).toEqual([
      'This field is required.',
      'Use 0 or more.',
    ])

    await wrapper.setProps({ modelValue: { name: 'Lab', seed: 7 } })
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('submit')).toEqual([[{ name: 'Lab', seed: 7 }]])
  })

  it('shows server field errors on their fields', () => {
    const wrapper = mount(SchemaForm, {
      props: {
        fields: [{ key: 'token', label: 'Token', schema: { type: 'string' } as ValueSchema }],
        modelValue: { token: 'x' },
        errors: { token: 'Some settings aren’t valid.' },
      },
      global: { plugins: [createAppI18n({ locale: 'en' })] },
    })
    expect(wrapper.get('[role="alert"]').text()).toBe('Some settings aren’t valid.')
  })
})
