import { useEffect, useState } from 'react'
import { AuthProvider } from './auth/AuthProvider'
import { useAuth } from './auth/context'
import { AuthForm } from './auth/AuthForm'
import { errorMessage } from './api/client'
import { useRoute } from './navigation'
import { PublicProfilePage } from './profiles/PublicProfilePage'
import { EditProfilePage } from './profiles/EditProfilePage'
import { UserSearchPage } from './profiles/UserSearchPage'
import './App.css'
function Screen() {
  const auth = useAuth()
  const route = useRoute()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  useEffect(() => {
    if (auth.status === 'ready' && auth.user && ['/login', '/register', '/'].includes(route))
      window.location.hash = '/app'
  }, [auth.status, auth.user, route])
  async function logout() {
    setBusy(true); setError('')
    try { await auth.logout(); window.location.hash = '/login' }
    catch (cause) { setError(errorMessage(cause)) }
    finally { setBusy(false) }
  }
  return <>
    <header className="site-header"><a className="brand" href="#/app">Realtime Chat</a>
      <nav aria-label="Κύρια πλοήγηση">
        {auth.user ? <><a href="#/profile">Το προφίλ μου</a><a href="#/users">Αναζήτηση</a><button onClick={logout} disabled={busy}>{busy ? 'Αποσύνδεση…' : 'Αποσύνδεση'}</button></>
          : <><a href="#/login">Σύνδεση</a><a href="#/register">Εγγραφή</a></>}
      </nav>
    </header>
    <main>
      {error && <p role="alert" className="error">{error}</p>}
      {route.startsWith('/profiles/') ? <PublicProfilePage key={route} id={route.slice('/profiles/'.length)} />
        : auth.status === 'loading' ? <p role="status">Έλεγχος συνεδρίας…</p>
        : auth.status === 'error' ? <section className="panel"><h1>Αδυναμία ελέγχου συνεδρίας</h1>
          <p role="alert">{auth.error}</p><button onClick={() => void auth.refresh()}>Δοκιμή ξανά</button></section>
        : !auth.user ? <AuthForm key={route === '/register' ? 'register' : 'login'} register={route === '/register'} />
        : route === '/profile' ? <EditProfilePage key={auth.user.id} />
        : route === '/users' ? <UserSearchPage key={auth.user.id} />
        : ['/app', '/', '/login', '/register'].includes(route)
          ? <section className="panel"><p className="eyebrow">Ο λογαριασμός σου</p><h1>Καλώς ήρθες, {auth.user.displayName ?? auth.user.userName}</h1>
            <p>Η σύνδεσή σου είναι ενεργή.</p><p>{auth.user.email}</p></section>
          : <section className="panel"><h1>Η σελίδα δεν βρέθηκε</h1><a href="#/app">Επιστροφή στον λογαριασμό</a></section>}
    </main>
  </>
}
export default function App() { return <AuthProvider><Screen /></AuthProvider> }
