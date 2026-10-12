import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}))

vi.mock('@tanstack/react-router', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@tanstack/react-router')>()),
  createFileRoute: () => () => ({}),
}))

const mutateAsync = vi.fn().mockResolvedValue({})
vi.mock('../../../features/admin/menus/menuAdminMutations', () => ({
  useCreateMenuMutation: () => ({ mutateAsync }),
  useToggleMenuActiveMutation: () => ({ mutate: vi.fn() }),
}))
vi.mock('../../../features/admin/menus/IconPickerSection', () => ({
  IconPickerSection: () => null,
}))

// Imports must come AFTER vi.mock so the module binds the stubs.
import { CreateMenuForm } from '../admin/menus'

describe('CreateMenuForm isPublic payload', () => {
  afterEach(() => {
    cleanup()
    mutateAsync.mockClear()
  })

  it('sends isPublic: false by default', async () => {
    render(<CreateMenuForm onDone={vi.fn()} topLevelMenus={[]} />)
    fireEvent.change(screen.getByLabelText('admin.menus.nameLabel'), { target: { value: 'Home' } })
    fireEvent.click(screen.getByRole('button', { name: 'admin.menus.saveButton' }))
    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    expect(mutateAsync.mock.calls[0][0]).toMatchObject({ name: 'Home', isPublic: false })
  })

  it('sends isPublic: true when "Is Public" is checked', async () => {
    render(<CreateMenuForm onDone={vi.fn()} topLevelMenus={[]} />)
    fireEvent.change(screen.getByLabelText('admin.menus.nameLabel'), { target: { value: 'Home' } })
    fireEvent.click(screen.getByLabelText('admin.menus.isPublicLabel'))
    fireEvent.click(screen.getByRole('button', { name: 'admin.menus.saveButton' }))
    await waitFor(() => expect(mutateAsync).toHaveBeenCalledTimes(1))
    expect(mutateAsync.mock.calls[0][0]).toMatchObject({ name: 'Home', isPublic: true })
  })
})
